using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.DiscMenus.Disc;

/// <summary>Everything the plugin needs to know about one disc, from a cheap managed scan.</summary>
public sealed class BlurayDisc
{
    public string SourcePath { get; init; } = string.Empty;

    public bool IsIso { get; init; }

    public DiscIndex Index { get; init; } = new();

    /// <summary>Gets a value indicating whether the disc uses BD-J (index objects or a BDMV/JAR folder).</summary>
    public bool UsesBdj { get; init; }

    /// <summary>Gets a value indicating whether the disc is still AACS encrypted (cannot be played).</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Gets every parseable playlist, sorted by number.</summary>
    public IReadOnlyList<Playlist> Playlists { get; init; } = Array.Empty<Playlist>();

    /// <summary>Gets the main feature, or null when there are no playable playlists.</summary>
    public Playlist? MainFeature { get; init; }

    /// <summary>Gets the playlists worth offering as separate versions (main feature first).</summary>
    public IReadOnlyList<Playlist> Titles { get; init; } = Array.Empty<Playlist>();

    /// <summary>Gets a short description for logs and the dashboard.</summary>
    public string MenuKind => UsesBdj ? (Index.HasHdmv ? "BD-J + HDMV" : "BD-J") : "HDMV";
}

/// <summary>Scans discs and caches the result by path and modification time.</summary>
public static class BlurayDiscScanner
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>Scans the disc at a library item path. Returns null if it is not a Blu-ray.</summary>
    public static BlurayDisc? TryScan(string? path, int minTitleSeconds = 120, int maxTitles = 15)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        DateTime stamp;
        try
        {
            if (File.Exists(path))
            {
                stamp = File.GetLastWriteTimeUtc(path);
            }
            else if (Directory.Exists(path))
            {
                stamp = Directory.GetLastWriteTimeUtc(path);
            }
            else
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp && hit.MinSeconds == minTitleSeconds && hit.MaxTitles == maxTitles)
        {
            return hit.Disc;
        }

        BlurayDisc? disc = null;
        try
        {
            using var fs = DiscFileSystem.TryOpen(path);
            if (fs != null)
            {
                disc = Scan(fs, minTitleSeconds, maxTitles);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            disc = null;
        }

        Cache[path] = new CacheEntry(stamp, minTitleSeconds, maxTitles, disc);
        return disc;
    }

    /// <summary>Scans an opened disc.</summary>
    public static BlurayDisc Scan(IDiscFileSystem fs, int minTitleSeconds = 120, int maxTitles = 15)
    {
        var index = BdmvParsers.ParseIndex(fs.ReadAllBytes("BDMV/index.bdmv"));

        var playlists = new List<Playlist>();
        var files = fs.ListFiles("BDMV/PLAYLIST")
            .Where(f => f.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var pl = BdmvParsers.ParsePlaylist(file, fs.ReadAllBytes("BDMV/PLAYLIST/" + file));
                if (pl.Items.Count > 0 && pl.Items.All(i => fs.FileExists("BDMV/STREAM/" + i.ClipName + ".m2ts")))
                {
                    playlists.Add(pl);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or FileNotFoundException)
            {
                // Skip broken or decoy playlists.
            }
        }

        var titles = SelectTitles(playlists, minTitleSeconds, maxTitles);
        var main = titles.FirstOrDefault();
        var usesBdj = index.UsesBdj || fs.ListFiles("BDMV/JAR").Any(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

        return new BlurayDisc
        {
            SourcePath = fs.SourcePath,
            IsIso = fs.IsIso,
            Index = index,
            UsesBdj = usesBdj,
            IsEncrypted = DetectEncryption(fs, main),
            Playlists = playlists,
            MainFeature = main,
            Titles = titles,
        };
    }

    /// <summary>
    /// Picks the main feature and the extras. Drops playlists that loop the same clip (menu backgrounds,
    /// obfuscation decoys) and exact duplicates, then sorts by duration.
    /// </summary>
    internal static List<Playlist> SelectTitles(IReadOnlyList<Playlist> playlists, int minTitleSeconds, int maxTitles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<Playlist>();
        var ordered = playlists
            .OrderByDescending(p => p.DurationSeconds)
            .ThenByDescending(p => p.Chapters.Count)
            .ThenBy(p => p.Number);
        foreach (var pl in ordered)
        {
            if (pl.DurationSeconds < 1)
            {
                continue;
            }

            var maxRepeat = pl.Items.GroupBy(i => i.ClipName).Max(g => g.Count());
            if (maxRepeat > 3 && maxRepeat * 2 > pl.Items.Count)
            {
                continue; // looping playlist
            }

            var key = string.Join("|", pl.Items.Select(i => i.ClipName + ":" + i.InTime + "-" + i.OutTime));
            if (!seen.Add(key))
            {
                continue;
            }

            candidates.Add(pl);
        }

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var result = new List<Playlist> { candidates[0] };
        foreach (var pl in candidates.Skip(1))
        {
            if (result.Count >= Math.Max(1, maxTitles))
            {
                break;
            }

            if (pl.DurationSeconds >= minTitleSeconds)
            {
                result.Add(pl);
            }
        }

        return result;
    }

    /// <summary>
    /// Checks the copy-permission bits in the first source packets of the main clip. Decrypted copies have
    /// them cleared even when the AACS folder is still there.
    /// </summary>
    internal static bool DetectEncryption(IDiscFileSystem fs, Playlist? main)
    {
        var clip = main?.Items.FirstOrDefault()?.ClipName;
        if (clip == null)
        {
            return false;
        }

        try
        {
            var head = fs.ReadPrefix("BDMV/STREAM/" + clip + ".m2ts", 192 * 64);
            var encrypted = 0;
            var packets = 0;
            for (var o = 0; o + 192 <= head.Length; o += 192)
            {
                if (head[o + 4] != 0x47 || (head[o] >> 6) != 0)
                {
                    encrypted++;
                }

                packets++;
            }

            return packets > 0 && encrypted * 2 > packets;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed record CacheEntry(DateTime Stamp, int MinSeconds, int MaxTitles, BlurayDisc? Disc);
}
