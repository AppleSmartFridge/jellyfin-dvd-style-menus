using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscMenus.Media;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus.Providers;

/// <summary>
/// Fills in runtime and chapters for Blu-ray items from the main feature playlist, so a disc
/// shows a correct length and chapter list even though ffprobe cannot read an ISO.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public abstract class DiscChapterProviderBase<T> : ICustomMetadataProvider<T>
    where T : Video
{
    private readonly IChapterManager _chapterManager;
    private readonly ILogger _logger;

    protected DiscChapterProviderBase(IChapterManager chapterManager, ILogger logger)
    {
        _chapterManager = chapterManager;
        _logger = logger;
    }

    public string Name => "Disc Menus (Blu-ray structure)";

    public Task<ItemUpdateType> FetchAsync(T item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        var update = ItemUpdateType.None;
        try
        {
            var disc = DiscMediaSourceProvider.ScanItem(item);
            var main = disc?.MainFeature;
            if (main == null)
            {
                return Task.FromResult(update);
            }

            var ticks = TimeSpan.FromSeconds(main.DurationSeconds).Ticks;
            if (item.RunTimeTicks is null or 0)
            {
                item.RunTimeTicks = ticks;
                update |= ItemUpdateType.MetadataImport;
            }

            if (main.Chapters.Count > 1 && _chapterManager.GetChapters(item.Id).Count == 0)
            {
                var chapters = main.Chapters
                    .Select((t, i) => new ChapterInfo
                    {
                        StartPositionTicks = TimeSpan.FromSeconds(t).Ticks,
                        Name = "Chapter " + (i + 1).ToString(CultureInfo.InvariantCulture),
                    })
                    .ToList();
                _chapterManager.SaveChapters(item, chapters);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Blu-ray chapters for {Path}", item.Path);
        }

        return Task.FromResult(update);
    }
}

/// <summary>Chapter provider for movies.</summary>
public sealed class MovieDiscChapterProvider : DiscChapterProviderBase<Movie>
{
    public MovieDiscChapterProvider(IChapterManager chapterManager, ILogger<MovieDiscChapterProvider> logger)
        : base(chapterManager, logger)
    {
    }
}

/// <summary>Chapter provider for episodes.</summary>
public sealed class EpisodeDiscChapterProvider : DiscChapterProviderBase<Episode>
{
    public EpisodeDiscChapterProvider(IChapterManager chapterManager, ILogger<EpisodeDiscChapterProvider> logger)
        : base(chapterManager, logger)
    {
    }
}

/// <summary>Chapter provider for plain videos (home video libraries).</summary>
public sealed class VideoDiscChapterProvider : DiscChapterProviderBase<Video>
{
    public VideoDiscChapterProvider(IChapterManager chapterManager, ILogger<VideoDiscChapterProvider> logger)
        : base(chapterManager, logger)
    {
    }
}
