using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.DiscMenus.Disc;
using Jellyfin.Plugin.DiscMenus.Media;
using Jellyfin.Plugin.DiscMenus.Native;
using Jellyfin.Plugin.DiscMenus.Sessions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace Jellyfin.Plugin.DiscMenus.Api;

/// <summary>REST API for the Disc Menus remote, the web integration and the internal stream URLs.</summary>
[ApiController]
[Route("DiscMenus")]
public class DiscMenusController : ControllerBase
{
    private const string RemoteHeader = "X-DiscMenus-Remote";

    private readonly MenuSessionManager _sessions;
    private readonly PairingService _pairing;
    private readonly NativeRuntimeManager _runtime;
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;

    public DiscMenusController(
        MenuSessionManager sessions,
        PairingService pairing,
        NativeRuntimeManager runtime,
        IAuthorizationContext authContext,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IServerApplicationHost appHost)
    {
        _sessions = sessions;
        _pairing = pairing;
        _runtime = runtime;
        _authContext = authContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _appHost = appHost;
    }

    // ---------------------------------------------------------------- remote control

    /// <summary>Lists the menu sessions the caller may control.</summary>
    [HttpGet("Sessions")]
    [AllowAnonymous]
    public async Task<ActionResult> GetSessions()
    {
        var user = await GetCallerAsync().ConfigureAwait(false);
        if (user == null)
        {
            return Unauthorized();
        }

        _sessions.RefreshOwnership();
        var list = _sessions.Sessions.Where(s => CanControl(user, s)).Select(Describe).ToList();
        return Ok(list);
    }

    /// <summary>Finds the menu session a Jellyfin device is playing (used by the web OSD button).</summary>
    [HttpGet("Sessions/ForDevice")]
    [AllowAnonymous]
    public async Task<ActionResult> GetSessionForDevice([FromQuery] string deviceId)
    {
        var user = await GetCallerAsync().ConfigureAwait(false);
        if (user == null)
        {
            return Unauthorized();
        }

        var s = string.IsNullOrEmpty(deviceId) ? null : _sessions.FindForDevice(deviceId);
        if (s == null || !CanControl(user, s))
        {
            return NoContent();
        }

        return Ok(Describe(s));
    }

    /// <summary>Sends a remote key: up, down, left, right, enter, menu, popup, back, 0-9, red, green, yellow, blue, play, pause, stop, next, prev.</summary>
    [HttpPost("Sessions/{id}/Key/{key}")]
    [AllowAnonymous]
    public async Task<ActionResult> SendKey([FromRoute] string id, [FromRoute] string key)
    {
        var (session, error) = await ResolveSessionAsync(id).ConfigureAwait(false);
        if (session == null)
        {
            return error!;
        }

        return await session.SendKeyAsync(key).ConfigureAwait(false) ? NoContent() : BadRequest("Unknown key or session ended.");
    }

    /// <summary>Sends a pointer move or click (BD-J and pop-up menus). x and y run from 0 to 1.</summary>
    [HttpPost("Sessions/{id}/Mouse")]
    [AllowAnonymous]
    public async Task<ActionResult> SendMouse([FromRoute] string id, [FromBody] MouseRequest request)
    {
        var (session, error) = await ResolveSessionAsync(id).ConfigureAwait(false);
        if (session == null)
        {
            return error!;
        }

        return await session.SendMouseAsync(request.X, request.Y, request.Click).ConfigureAwait(false) ? NoContent() : BadRequest();
    }

    /// <summary>Sends a player command: title (number), audio (1-based), subtitle (0 = off), chapter (+1/-1).</summary>
    [HttpPost("Sessions/{id}/Command")]
    [AllowAnonymous]
    public async Task<ActionResult> SendCommand([FromRoute] string id, [FromBody] CommandRequest request)
    {
        var (session, error) = await ResolveSessionAsync(id).ConfigureAwait(false);
        if (session == null)
        {
            return error!;
        }

        return await session.SendCommandAsync(request.Command ?? string.Empty, request.Value).ConfigureAwait(false) ? NoContent() : BadRequest();
    }

    /// <summary>Server-sent events with the disc state (title, menu shown, audio, subtitles...).</summary>
    [HttpGet("Sessions/{id}/Events")]
    [AllowAnonymous]
    public async Task Events([FromRoute] string id, CancellationToken cancellationToken)
    {
        var (session, error) = await ResolveSessionAsync(id).ConfigureAwait(false);
        if (session == null)
        {
            Response.StatusCode = error is UnauthorizedResult ? 401 : 404;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        var reader = session.SubscribeState(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
                string payload;
                try
                {
                    payload = await reader.ReadAsync(heartbeat.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await Response.WriteAsync(": ping\n\n", cancellationToken).ConfigureAwait(false);
                    await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await Response.WriteAsync("data: " + payload + "\n\n", cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
        }
    }

    // ---------------------------------------------------------------- pairing / QR

    /// <summary>Creates a 10-minute pairing code for the phone remote, with its URL and QR code address.</summary>
    [HttpPost("Pairing")]
    [Authorize]
    public async Task<ActionResult> CreatePairing()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated || auth.User == null)
        {
            return Unauthorized();
        }

        var (code, expires) = _pairing.CreatePairCode(auth.User.Id);
        var baseUrl = _appHost.GetSmartApiUrl(Request).TrimEnd('/');
        return Ok(new
        {
            Code = code,
            Expires = expires,
            RemoteUrl = $"{baseUrl}/DiscMenus/Remote?pair={code}",
            QrUrl = $"{baseUrl}/DiscMenus/Qr.svg?pair={code}",
        });
    }

    /// <summary>Exchanges a pairing code (once) for a 24-hour remote token.</summary>
    [HttpPost("Pairing/Claim")]
    [AllowAnonymous]
    public ActionResult ClaimPairing([FromBody] ClaimRequest request)
    {
        var claim = _pairing.Claim(request.Code);
        if (claim == null)
        {
            return BadRequest("This pairing link has expired or was already used. Make a new one on the Disc Menus page.");
        }

        var user = _userManager.GetUserById(claim.Value.UserId);
        return Ok(new { RemoteToken = claim.Value.Token, UserName = user?.Username });
    }

    /// <summary>QR code (SVG) for a pairing link. Only works for a valid, unexpired pairing code.</summary>
    [HttpGet("Qr.svg")]
    [AllowAnonymous]
    public ActionResult GetQr([FromQuery] string pair)
    {
        if (!_pairing.IsValidPairCode(pair))
        {
            return NotFound();
        }

        var url = $"{_appHost.GetSmartApiUrl(Request).TrimEnd('/')}/DiscMenus/Remote?pair={pair}";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(6);
        Response.Headers.CacheControl = "no-store";
        return Content(svg, "image/svg+xml", Encoding.UTF8);
    }

    // ---------------------------------------------------------------- static web files

    /// <summary>The phone remote page.</summary>
    [HttpGet("Remote")]
    [AllowAnonymous]
    public ActionResult GetRemotePage() => Resource("Web.remote.html", MediaTypeNames.Text.Html);

    [HttpGet("Remote/remote.js")]
    [AllowAnonymous]
    public ActionResult GetRemoteScript() => Resource("Web.remote.js", "application/javascript");

    [HttpGet("Remote/remote.css")]
    [AllowAnonymous]
    public ActionResult GetRemoteStyles() => Resource("Web.remote.css", "text/css");

    /// <summary>Script added to jellyfin-web: the OSD "Disc Remote" button, the web remote panel and optional key capture.</summary>
    [HttpGet("web-inject.js")]
    [AllowAnonymous]
    public ActionResult GetWebInject() => Resource("Web.web-inject.js", "application/javascript");

    /// <summary>Settings the web script needs.</summary>
    [HttpGet("ClientConfig")]
    [AllowAnonymous]
    public ActionResult GetClientConfig()
    {
        var c = Plugin.Instance?.Configuration;
        return Ok(new
        {
            WebIntegration = c?.EnableWebIntegration ?? true,
            KeyCapture = c?.EnableWebKeyCapture ?? false,
        });
    }

    // ---------------------------------------------------------------- admin

    /// <summary>Runtime and session status for the settings page.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus()
    {
        var rt = _runtime.TryGetInstalled();
        _sessions.RefreshOwnership();
        return Ok(new
        {
            Rid = NativeRuntimeManager.CurrentRid,
            RuntimeVersion = _runtime.RuntimeVersion,
            RuntimeInstalled = rt != null,
            RuntimeSource = rt?.Source,
            RuntimePath = rt?.Root,
            BdjAvailable = rt?.JavaHome != null && rt.BdjJar != null,
            LastError = _runtime.LastError,
            Sessions = _sessions.Sessions.Select(Describe).ToList(),
        });
    }

    /// <summary>Downloads and installs the runtime for this server now.</summary>
    [HttpPost("Runtime/Install")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> InstallRuntime(CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.InstallAsync(cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Http.HttpRequestException)
        {
            return Problem(ex.Message);
        }
    }

    /// <summary>Stops a menu session.</summary>
    [HttpDelete("Sessions/{id}")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult StopSession([FromRoute] string id)
    {
        _sessions.Stop(id);
        return NoContent();
    }

    /// <summary>What the plugin sees on a disc (for troubleshooting).</summary>
    [HttpGet("Discs/{itemId}")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetDisc([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound();
        }

        var disc = DiscMediaSourceProvider.ScanItem(item);
        if (disc == null)
        {
            return Ok(new { item.Path, IsBluray = false });
        }

        return Ok(new
        {
            item.Path,
            IsBluray = true,
            disc.IsIso,
            disc.IsEncrypted,
            disc.MenuKind,
            FirstPlayback = disc.Index.FirstPlayback.ToString(),
            TopMenu = disc.Index.TopMenu.ToString(),
            Titles = disc.Index.Titles.Select(t => t.ToString()),
            MainFeature = disc.MainFeature?.Name,
            Playlists = disc.Playlists.Select(p => new
            {
                p.Name,
                p.DurationSeconds,
                Clips = p.Items.Select(i => i.ClipName),
                Chapters = p.Chapters.Count,
                Streams = p.Streams.Select(s => new { Kind = s.Kind.ToString(), s.Pid, Coding = "0x" + s.CodingType.ToString("X2", System.Globalization.CultureInfo.InvariantCulture), s.Language }),
            }),
        });
    }

    // ---------------------------------------------------------------- internal streams (read by Jellyfin's ffmpeg)

    /// <summary>Live MPEG-TS output of a menu session. The session id is the secret.</summary>
    [HttpGet("Live/{id}/stream.ts")]
    [AllowAnonymous]
    public async Task GetLiveStream([FromRoute] string id, CancellationToken cancellationToken)
    {
        var session = _sessions.Get(id);
        if (session == null)
        {
            Response.StatusCode = 404;
            return;
        }

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-cache";
        await using var stream = session.Broadcaster.Subscribe();
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                session.LastActivity = DateTime.UtcNow;
                await Response.Body.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A playlist's clips joined as one seekable m2ts file. Needs the signed token.</summary>
    [HttpGet("Titles/{itemId}/{playlist}/stream.m2ts")]
    [HttpHead("Titles/{itemId}/{playlist}/stream.m2ts")]
    [AllowAnonymous]
    public ActionResult GetTitleStream([FromRoute] Guid itemId, [FromRoute] int playlist, [FromQuery] string? token)
    {
        if (!StreamUrls.Verify(itemId, playlist, token))
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item == null || !DiscMediaSourceProvider.IsCandidate(item))
        {
            return NotFound();
        }

        using var fs = DiscFileSystem.TryOpen(item.Path);
        if (fs == null)
        {
            return NotFound();
        }

        var name = playlist.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".mpls";
        var pl = BdmvParsers.ParsePlaylist(name, fs.ReadAllBytes("BDMV/PLAYLIST/" + name));
        var extents = pl.Items.SelectMany(i => fs.GetExtents("BDMV/STREAM/" + i.ClipName + ".m2ts")).ToList();
        var stream = new VirtualConcatStream(extents);
        return File(stream, "video/MP2T", enableRangeProcessing: true);
    }

    // ---------------------------------------------------------------- helpers

    private static object Describe(MenuSession s) => new
    {
        s.Id,
        ItemId = s.ItemId.ToString("N"),
        s.ItemName,
        s.MenuKind,
        s.Created,
        Viewers = s.DeviceIds.Count,
        State = s.GetState(),
    };

    private bool CanControl(User user, MenuSession s)
    {
        if (user.HasPermission(PermissionKind.IsAdministrator))
        {
            return true;
        }

        if (s.OwnerUserId.HasValue)
        {
            return s.OwnerUserId.Value == user.Id;
        }

        var item = _libraryManager.GetItemById(s.ItemId);
        return item != null && item.IsVisible(user, false);
    }

    private async Task<User?> GetCallerAsync()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (auth.IsAuthenticated && auth.User != null)
        {
            return auth.User;
        }

        string? token = Request.Headers[RemoteHeader];
        if (string.IsNullOrEmpty(token))
        {
            token = Request.Query["rt"];
        }

        var userId = _pairing.ValidateRemoteToken(token);
        return userId.HasValue ? _userManager.GetUserById(userId.Value) : null;
    }

    private async Task<(MenuSession? Session, ActionResult? Error)> ResolveSessionAsync(string id)
    {
        var user = await GetCallerAsync().ConfigureAwait(false);
        if (user == null)
        {
            return (null, Unauthorized());
        }

        var s = _sessions.Get(id);
        if (s == null || !CanControl(user, s))
        {
            return (null, NotFound());
        }

        s.LastActivity = DateTime.UtcNow;
        return (s, null);
    }

    private ActionResult Resource(string name, string contentType)
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.DiscMenus." + name);
        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, contentType + "; charset=utf-8");
    }

    /// <summary>Pointer request body.</summary>
    public sealed class MouseRequest
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("click")]
        public bool Click { get; set; } = true;
    }

    /// <summary>Command request body.</summary>
    public sealed class CommandRequest
    {
        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("value")]
        public int Value { get; set; }
    }

    /// <summary>Pairing claim body.</summary>
    public sealed class ClaimRequest
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }
}
