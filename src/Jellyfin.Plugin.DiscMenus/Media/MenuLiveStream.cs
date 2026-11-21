using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscMenus.Sessions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.DiscMenus.Media;

/// <summary>
/// A Jellyfin live stream backed by a menu session. Jellyfin's transcoder reads the helper's MPEG-TS
/// output over a local URL (or through <see cref="GetStream"/>) and serves it to the client in whatever
/// format that client supports.
/// </summary>
public sealed class MenuLiveStream : ILiveStream, IDirectStreamProvider
{
    private readonly MenuSessionManager _manager;
    private readonly Func<CancellationToken, Task<MenuSession>> _start;
    private MenuSession? _session;

    public MenuLiveStream(MenuSessionManager manager, MediaSourceInfo mediaSource, Func<CancellationToken, Task<MenuSession>> start)
    {
        _manager = manager;
        MediaSource = mediaSource;
        _start = start;
        UniqueId = Guid.NewGuid().ToString("N");
        OriginalStreamId = UniqueId;
    }

    public int ConsumerCount { get; set; }

    public string OriginalStreamId { get; set; }

    public string TunerHostId => string.Empty;

    public bool EnableStreamSharing => false;

    public MediaSourceInfo MediaSource { get; set; }

    public string UniqueId { get; }

    public MenuSession? Session => _session;

    public async Task Open(CancellationToken openCancellationToken)
    {
        _session = await _start(openCancellationToken).ConfigureAwait(false);
        OriginalStreamId = _session.Id;
    }

    public Task Close()
    {
        if (_session != null)
        {
            _manager.Stop(_session.Id);
        }

        return Task.CompletedTask;
    }

    public Stream GetStream()
    {
        var session = _session ?? throw new InvalidOperationException("The disc menu stream is not open.");
        session.LastActivity = DateTime.UtcNow;
        return session.Broadcaster.Subscribe();
    }

    public void Dispose()
    {
        Close().GetAwaiter().GetResult();
    }
}
