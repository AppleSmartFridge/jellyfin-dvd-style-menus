using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscMenus.Disc;
using Jellyfin.Plugin.DiscMenus.Sessions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus.Media;

/// <summary>
/// Adds versions to Blu-ray items: "Disc Menu (Interactive)" (a live stream rendered on the server)
/// and one version per main title or extra. They appear in every client's version picker.
/// </summary>
public sealed class DiscMediaSourceProvider : IMediaSourceProvider
{
    internal const string MenuIdPrefix = "dm-menu-";
    internal const string TitleIdPrefix = "dm-title-";

    private readonly ILibraryManager _libraryManager;
    private readonly MenuSessionManager _sessions;
    private readonly StreamUrls _urls;
    private readonly ILogger<DiscMediaSourceProvider> _logger;

    public DiscMediaSourceProvider(ILibraryManager libraryManager, MenuSessionManager sessions, StreamUrls urls, ILogger<DiscMediaSourceProvider> logger)
    {
        _libraryManager = libraryManager;
        _sessions = sessions;
        _urls = urls;
        _logger = logger;
    }

    /// <summary>Returns true if a library item may be a Blu-ray (folder or ISO).</summary>
    public static bool IsCandidate(BaseItem item)
    {
        if (item is not Video video || video.IsPlaceHolder || !video.IsFileProtocol || string.IsNullOrEmpty(video.Path))
        {
            return false;
        }

        return video.VideoType == VideoType.BluRay
            || (video.VideoType == VideoType.Iso && video.IsoType != IsoType.Dvd)
            || video.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
    }

    public static BlurayDisc? ScanItem(BaseItem item)
    {
        if (!IsCandidate(item))
        {
            return null;
        }

        var cfg = Plugin.Instance?.Configuration;
        return BlurayDiscScanner.TryScan(item.Path, cfg?.MinTitleSeconds ?? 120, cfg?.MaxTitleVersions ?? 15);
    }

    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var result = new List<MediaSourceInfo>();
        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        if (!cfg.EnableMenus && !cfg.EnableTitleVersions)
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(result);
        }

        BlurayDisc? disc;
        try
        {
            disc = ScanItem(item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Blu-ray structure for {Path}", item.Path);
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(result);
        }

        if (disc == null || disc.MainFeature == null)
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(result);
        }

        if (disc.IsEncrypted)
        {
            _logger.LogWarning("{Path} is still AACS encrypted; Disc Menus only plays decrypted discs", item.Path);
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(result);
        }

        var itemN = item.Id.ToString("N");
        if (cfg.EnableMenus)
        {
            result.Add(BuildMenuSource(itemN, disc, pendingPath: true, url: null));
        }

        if (cfg.EnableTitleVersions)
        {
            var first = true;
            foreach (var pl in disc.Titles)
            {
                result.Add(BuildTitleSource(item.Id, pl, first));
                first = false;
            }
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(result);
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // openToken = "menu|<item id N>"
        var parts = openToken.Split('|');
        if (parts.Length != 2 || parts[0] != "menu" || !Guid.TryParseExact(parts[1], "N", out var itemId))
        {
            throw new ArgumentException("Unknown Disc Menus open token.", nameof(openToken));
        }

        var item = _libraryManager.GetItemById(itemId) ?? throw new ArgumentException("Item not found.", nameof(openToken));
        var disc = ScanItem(item) ?? throw new InvalidOperationException("Item is not a readable Blu-ray.");
        var source = BuildMenuSource(parts[1], disc, pendingPath: false, url: null);

        MenuLiveStream? live = null;
        live = new MenuLiveStream(_sessions, source, async ct =>
        {
            var session = await _sessions.StartAsync(itemId, item.Name, disc, ct).ConfigureAwait(false);
            live!.MediaSource.Path = _urls.LiveUrl(session.Id);
            return session;
        });
        return Task.FromResult<ILiveStream>(live);
    }

    private static MediaSourceInfo BuildMenuSource(string itemN, BlurayDisc disc, bool pendingPath, string? url)
    {
        return new MediaSourceInfo
        {
            Id = MenuIdPrefix + itemN,
            Name = $"Disc Menu (Interactive, {disc.MenuKind})",
            Protocol = MediaProtocol.Http,
            Path = url ?? "http://127.0.0.1/DiscMenus/pending",
            Container = "ts",
            Type = MediaSourceType.Default,
            IsRemote = false,
            IsInfiniteStream = true,
            RequiresOpening = pendingPath,
            OpenToken = pendingPath ? "menu|" + itemN : null,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            SupportsProbing = false,
            GenPtsInput = true,
            ReadAtNativeFramerate = false,
            AnalyzeDurationMs = 1500,
            VideoType = VideoType.VideoFile,
            Bitrate = ((Plugin.Instance?.Configuration.MenuVideoBitrateKbps ?? 10000) + 256) * 1000,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Profile = "High",
                    Width = 1920,
                    Height = 1080,
                    AverageFrameRate = 23.976f,
                    RealFrameRate = 23.976f,
                    PixelFormat = "yuv420p",
                    BitDepth = 8,
                    IsInterlaced = false,
                    AspectRatio = "16:9",
                    IsDefault = true,
                },
                new()
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    ChannelLayout = "stereo",
                    SampleRate = 48000,
                    BitRate = 192000,
                    IsDefault = true,
                },
            },
        };
    }

    private MediaSourceInfo BuildTitleSource(Guid itemId, Playlist pl, bool isMain)
    {
        var duration = TimeSpan.FromSeconds(pl.DurationSeconds);
        var label = isMain ? "Main Feature" : "Title";
        var name = string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1:D5} ({2}h {3:D2}m)",
            label,
            pl.Number,
            (int)duration.TotalHours,
            duration.Minutes);

        return new MediaSourceInfo
        {
            Id = TitleIdPrefix + itemId.ToString("N") + "-" + pl.Number.ToString("D5", CultureInfo.InvariantCulture),
            Name = name,
            Protocol = MediaProtocol.Http,
            Path = _urls.TitleUrl(itemId, pl.Number),
            Container = "m2ts",
            Type = MediaSourceType.Default,
            IsRemote = false,
            RunTimeTicks = duration.Ticks,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            SupportsProbing = true,
            GenPtsInput = true,
            VideoType = VideoType.VideoFile,
            MediaStreams = BuildStreams(pl),
        };
    }

    internal static List<MediaStream> BuildStreams(Playlist pl)
    {
        var streams = new List<MediaStream>();
        var index = 0;
        foreach (var s in pl.Streams.Where(s => s.Kind == StreamKind.Video).Take(1))
        {
            var (w, h, interlaced) = VideoSize(s.VideoFormat);
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = index++,
                Codec = VideoCodec(s.CodingType),
                Width = w,
                Height = h,
                IsInterlaced = interlaced,
                AverageFrameRate = FrameRate(s.Rate),
                RealFrameRate = FrameRate(s.Rate),
                IsDefault = true,
            });
        }

        var firstAudio = true;
        foreach (var s in pl.Streams.Where(s => s.Kind == StreamKind.Audio))
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = index++,
                Codec = AudioCodec(s.CodingType),
                Language = string.IsNullOrEmpty(s.Language) ? null : s.Language,
                Channels = AudioChannels(s.VideoFormat),
                SampleRate = 48000,
                IsDefault = firstAudio,
            });
            firstAudio = false;
        }

        foreach (var s in pl.Streams.Where(s => s.Kind == StreamKind.PresentationGraphics))
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = index++,
                Codec = "pgssub",
                Language = string.IsNullOrEmpty(s.Language) ? null : s.Language,
            });
        }

        return streams;
    }

    private static string VideoCodec(int coding) => coding switch
    {
        0x1B => "h264",
        0x24 => "hevc",
        0xEA => "vc1",
        0x01 => "mpeg1video",
        _ => "mpeg2video",
    };

    private static string AudioCodec(int coding) => coding switch
    {
        0x80 => "pcm_bluray",
        0x81 => "ac3",
        0x82 => "dts",
        0x83 => "truehd",
        0x84 or 0xA1 => "eac3",
        0x85 or 0x86 or 0xA2 => "dts",
        0x03 => "mp1",
        0x04 => "mp2",
        _ => "ac3",
    };

    private static (int W, int H, bool Interlaced) VideoSize(int format) => format switch
    {
        1 => (720, 480, true),
        2 => (720, 576, true),
        3 => (720, 480, false),
        4 => (1920, 1080, true),
        5 => (1280, 720, false),
        6 => (1920, 1080, false),
        7 => (720, 576, false),
        8 => (3840, 2160, false),
        _ => (1920, 1080, false),
    };

    private static float FrameRate(int rate) => rate switch
    {
        1 => 23.976f,
        2 => 24f,
        3 => 25f,
        4 => 29.97f,
        6 => 50f,
        7 => 59.94f,
        _ => 23.976f,
    };

    private static int AudioChannels(int format) => format switch
    {
        1 => 1,
        3 => 2,
        6 => 6,
        12 => 8,
        _ => 2,
    };
}
