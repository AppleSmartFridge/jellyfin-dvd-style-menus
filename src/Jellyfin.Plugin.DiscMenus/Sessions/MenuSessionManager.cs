using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscMenus.Disc;
using Jellyfin.Plugin.DiscMenus.Native;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus.Sessions;

/// <summary>Starts, tracks and cleans up menu sessions (one bdmenu helper per playback).</summary>
public sealed class MenuSessionManager : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, MenuSession> _sessions = new(StringComparer.Ordinal);
    private readonly NativeRuntimeManager _runtime;
    private readonly IApplicationPaths _paths;
    private readonly ISessionManager _jellyfinSessions;
    private readonly ILogger<MenuSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private Timer? _timer;

    public MenuSessionManager(
        NativeRuntimeManager runtime,
        IApplicationPaths paths,
        ISessionManager jellyfinSessions,
        ILoggerFactory loggerFactory)
    {
        _runtime = runtime;
        _paths = paths;
        _jellyfinSessions = jellyfinSessions;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MenuSessionManager>();
    }

    public IReadOnlyCollection<MenuSession> Sessions => _sessions.Values.ToList();

    public MenuSession? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>Starts a helper for a disc and returns the running session.</summary>
    public async Task<MenuSession> StartAsync(Guid itemId, string itemName, BlurayDisc disc, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        if (disc.IsEncrypted)
        {
            throw new InvalidOperationException("This disc is still AACS encrypted. Disc Menus only plays decrypted copies.");
        }

        CleanupDead();
        if (_sessions.Count >= Math.Max(1, config.MaxConcurrentSessions))
        {
            throw new InvalidOperationException($"Too many disc menu sessions are running (limit {config.MaxConcurrentSessions}).");
        }

        var runtime = await _runtime.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(_paths.PluginConfigurationsPath, "DiscMenus");
        var persist = Path.Combine(dataRoot, "bdj-persistent");
        var cache = Path.Combine(_paths.CachePath, "discmenus", id);
        Directory.CreateDirectory(persist);
        Directory.CreateDirectory(cache);

        var psi = new ProcessStartInfo(runtime.HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cache,
        };
        psi.ArgumentList.Add("--menu");
        psi.ArgumentList.Add("--disc");
        psi.ArgumentList.Add(disc.SourcePath);
        psi.ArgumentList.Add("--vbitrate");
        psi.ArgumentList.Add(Math.Clamp(config.MenuVideoBitrateKbps, 1000, 60000).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--preset");
        psi.ArgumentList.Add(SafeToken(config.EncoderPreset, "veryfast"));
        psi.ArgumentList.Add("--encoder");
        psi.ArgumentList.Add(SafeToken(config.PreferredEncoder, "auto"));
        psi.ArgumentList.Add("--region");
        psi.ArgumentList.Add(config.RegionCode is "A" or "B" or "C" ? config.RegionCode : "A");
        psi.ArgumentList.Add("--lang");
        psi.ArgumentList.Add(SafeToken(config.PreferredLanguage, "eng"));
        psi.ArgumentList.Add("--persist");
        psi.ArgumentList.Add(persist);
        psi.ArgumentList.Add("--cache");
        psi.ArgumentList.Add(cache);
        if (config.AutoEnterSeconds > 0)
        {
            psi.ArgumentList.Add("--auto-enter");
            psi.ArgumentList.Add(config.AutoEnterSeconds.ToString(CultureInfo.InvariantCulture));
        }

        if (!disc.UsesBdj)
        {
            psi.ArgumentList.Add("--no-bdj");
        }

        foreach (var (k, v) in runtime.BuildEnvironment())
        {
            psi.Environment[k] = v;
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the bdmenu helper.");
        var session = new MenuSession(id, itemId, itemName, disc.MenuKind, process, _loggerFactory.CreateLogger("DiscMenus.Helper"));
        session.Exited += (_, _) => _logger.LogInformation("Disc menu session {Id} for {Item} ended", id, itemName);
        _sessions[id] = session;
        session.Start();
        _logger.LogInformation("Started {Kind} disc menu session {Id} for {Item} (runtime: {Source})", disc.MenuKind, id, itemName, runtime.Source);
        return session;
    }

    public void Stop(string id)
    {
        if (_sessions.TryRemove(id, out var s))
        {
            s.Dispose();
            TryDeleteCache(id);
        }
    }

    /// <summary>Matches menu sessions to the Jellyfin sessions playing them, to learn owners and devices.</summary>
    public void RefreshOwnership()
    {
        foreach (var menu in _sessions.Values)
        {
            var devices = new List<string>();
            foreach (var js in _jellyfinSessions.Sessions)
            {
                var ps = js.PlayState;
                var matches = (ps?.LiveStreamId?.Contains(menu.Id, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (ps?.MediaSourceId?.Contains("dm-menu", StringComparison.OrdinalIgnoreCase) == true && js.NowPlayingItem?.Id == menu.ItemId);
                if (!matches)
                {
                    continue;
                }

                devices.Add(js.DeviceId);
                menu.OwnerUserId ??= js.UserId;
                menu.LastActivity = DateTime.UtcNow;
            }

            menu.DeviceIds = devices;
        }
    }

    /// <summary>Finds the menu session a Jellyfin device is playing, if any.</summary>
    public MenuSession? FindForDevice(string deviceId)
    {
        RefreshOwnership();
        return _sessions.Values.FirstOrDefault(s => s.DeviceIds.Contains(deviceId, StringComparer.Ordinal));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Housekeeping(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        foreach (var id in _sessions.Keys.ToList())
        {
            Stop(id);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var s in _sessions.Values)
        {
            s.Dispose();
        }

        _sessions.Clear();
    }

    private static string SafeToken(string? value, string fallback)
        => !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : fallback;

    private void Housekeeping()
    {
        try
        {
            RefreshOwnership();
            CleanupDead();
            var idle = TimeSpan.FromMinutes(Math.Max(1, Plugin.Instance?.Configuration.IdleTimeoutMinutes ?? 10));
            foreach (var s in _sessions.Values)
            {
                if (s.Broadcaster.ReaderCount == 0 && s.DeviceIds.Count == 0 && DateTime.UtcNow - s.LastActivity > idle)
                {
                    _logger.LogInformation("Stopping idle disc menu session {Id}", s.Id);
                    Stop(s.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disc menu housekeeping failed");
        }
    }

    private void CleanupDead()
    {
        foreach (var s in _sessions.Values.Where(s => s.HasExited).ToList())
        {
            Stop(s.Id);
        }
    }

    private void TryDeleteCache(string id)
    {
        try
        {
            var dir = Path.Combine(_paths.CachePath, "discmenus", id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
