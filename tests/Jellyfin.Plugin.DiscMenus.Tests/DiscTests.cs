using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DiscMenus.Disc;
using Jellyfin.Plugin.DiscMenus.Media;
using Jellyfin.Plugin.DiscMenus.Web;
using MediaBrowser.Model.Entities;
using Xunit;
using static Jellyfin.Plugin.DiscMenus.Tests.BdmvFixtures;

namespace Jellyfin.Plugin.DiscMenus.Tests;

public class IndexParserTests
{
    [Fact]
    public void Hdmv_disc_is_not_bdj()
    {
        var idx = BdmvParsers.ParseIndex(Index(1, 1, 1, 1));
        Assert.Equal(TitleObjectType.Hdmv, idx.FirstPlayback);
        Assert.Equal(2, idx.Titles.Count);
        Assert.False(idx.UsesBdj);
        Assert.True(idx.HasHdmv);
    }

    [Fact]
    public void Bdj_top_menu_is_detected()
    {
        var idx = BdmvParsers.ParseIndex(Index(1, 2, 1));
        Assert.Equal(TitleObjectType.Bdj, idx.TopMenu);
        Assert.True(idx.UsesBdj);
    }

    [Fact]
    public void Rejects_non_index_files()
    {
        Assert.Throws<InvalidDataException>(() => BdmvParsers.ParseIndex(new byte[64]));
    }
}

public class PlaylistParserTests
{
    [Fact]
    public void Reads_items_duration_streams_and_chapters()
    {
        var bytes = Playlist(
            new[] { new Item("00001", 10, 610), new Item("00002", 0, 300) },
            new[] { (0, 0.0), (0, 300.0), (1, 0.0), (1, 120.0) });
        var pl = BdmvParsers.ParsePlaylist("00800.mpls", bytes);

        Assert.Equal(800, pl.Number);
        Assert.Equal(2, pl.Items.Count);
        Assert.Equal("00001", pl.Items[0].ClipName);
        Assert.Equal(900, pl.DurationSeconds, 3);
        Assert.Equal(new[] { 0.0, 300.0, 600.0, 720.0 }, pl.Chapters);

        var video = Assert.Single(pl.Streams, s => s.Kind == StreamKind.Video);
        Assert.Equal(0x1011, video.Pid);
        Assert.Equal(0x1B, video.CodingType);
        var audio = Assert.Single(pl.Streams, s => s.Kind == StreamKind.Audio);
        Assert.Equal(0x1100, audio.Pid);
        Assert.Equal("eng", audio.Language);
    }

    [Fact]
    public void Builds_jellyfin_streams_from_stn()
    {
        var pl = BdmvParsers.ParsePlaylist("00001.mpls", Playlist(new[] { new Item("00001", 0, 60) }));
        var streams = DiscMediaSourceProvider.BuildStreams(pl);
        Assert.Equal("h264", streams[0].Codec);
        Assert.Equal(1920, streams[0].Width);
        Assert.Equal(MediaStreamType.Audio, streams[1].Type);
        Assert.Equal("ac3", streams[1].Codec);
        Assert.Equal("eng", streams[1].Language);
    }
}

public class TitleSelectionTests
{
    private static Playlist Pl(string name, params (string Clip, double Seconds)[] items)
        => new()
        {
            Name = name,
            Items = items.Select(i => new PlayItem(i.Clip, 0, (uint)(i.Seconds * 45000), false)).ToList(),
        };

    [Fact]
    public void Picks_longest_as_main_and_drops_duplicates_loops_and_short_titles()
    {
        var playlists = new List<Playlist>
        {
            Pl("00001.mpls", ("00010", 5400)),                     // main feature
            Pl("00002.mpls", ("00010", 5400)),                     // exact duplicate (decoy)
            Pl("00003.mpls", ("00020", 30), ("00020", 30), ("00020", 30), ("00020", 30), ("00020", 30)), // looping menu background
            Pl("00004.mpls", ("00030", 600)),                      // extra
            Pl("00005.mpls", ("00040", 45)),                       // too short
        };

        var titles = BlurayDiscScanner.SelectTitles(playlists, 120, 15);
        Assert.Equal(new[] { "00001.mpls", "00004.mpls" }, titles.Select(t => t.Name));
    }

    [Fact]
    public void Respects_maximum()
    {
        var playlists = Enumerable.Range(1, 10).Select(i => Pl($"{i:D5}.mpls", ($"{i:D5}", 1000 + i))).ToList();
        Assert.Equal(3, BlurayDiscScanner.SelectTitles(playlists, 120, 3).Count);
    }
}

public class FolderDiscTests : IDisposable
{
    private readonly List<string> _dirs = new();

    [Fact]
    public void Scans_folder_disc_end_to_end()
    {
        var root = Make(encrypted: false, jar: true);
        var disc = BlurayDiscScanner.TryScan(root);
        Assert.NotNull(disc);
        Assert.False(disc!.IsIso);
        Assert.True(disc.UsesBdj); // BDMV/JAR present
        Assert.False(disc.IsEncrypted);
        Assert.Equal("00001.mpls", disc.MainFeature!.Name);
        Assert.Equal(2, disc.Titles.Count);
    }

    [Fact]
    public void Accepts_bdmv_folder_and_index_path()
    {
        var root = Make(false, false);
        Assert.NotNull(BlurayDiscScanner.TryScan(Path.Combine(root, "BDMV")));
        Assert.NotNull(DiscFileSystem.TryOpen(Path.Combine(root, "BDMV", "index.bdmv")));
    }

    [Fact]
    public void Detects_encrypted_clips()
    {
        var disc = BlurayDiscScanner.TryScan(Make(encrypted: true, jar: false));
        Assert.True(disc!.IsEncrypted);
    }

    [Fact]
    public void Not_a_disc_returns_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dm-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        Assert.Null(BlurayDiscScanner.TryScan(dir));
    }

    [Fact]
    public void Concat_stream_joins_clips_and_seeks()
    {
        var root = Make(false, false);
        using var fs = DiscFileSystem.TryOpen(root)!;
        var extents = new[] { "00010", "00030" }.SelectMany(c => fs.GetExtents($"BDMV/STREAM/{c}.m2ts")).ToList();
        using var s = new VirtualConcatStream(extents);
        Assert.Equal((100 + 50) * 192, s.Length);

        s.Position = (100 * 192) + 4; // first packet of the second clip
        Assert.Equal(0x47, s.ReadByte());

        s.Position = 0;
        var all = new byte[s.Length];
        var read = 0;
        int n;
        while ((n = s.Read(all, read, all.Length - read)) > 0)
        {
            read += n;
        }

        Assert.Equal(s.Length, read);
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try
            {
                Directory.Delete(d, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string Make(bool encrypted, bool jar)
    {
        var root = WriteFolder(
            Index(1, 1, 1),
            new Dictionary<string, byte[]>
            {
                ["00001.mpls"] = Playlist(new[] { new Item("00010", 0, 5400) }, new[] { (0, 0.0), (0, 600.0) }),
                ["00002.mpls"] = Playlist(new[] { new Item("00030", 0, 900) }),
                ["00003.mpls"] = Playlist(new[] { new Item("00099", 0, 900) }), // clip missing: ignored
            },
            new Dictionary<string, byte[]>
            {
                ["00010.m2ts"] = Clip(100, encrypted),
                ["00030.m2ts"] = Clip(50, encrypted),
            },
            jar);
        _dirs.Add(root);
        return root;
    }
}

public class WebIntegrationTests
{
    [Fact]
    public void Inserts_script_once_before_body_end()
    {
        var html = "<html><body><div></div></body></html>";
        var once = WebIntegration.Transform(new TransformPayload { Contents = html });
        Assert.Contains(WebIntegration.ScriptTag + "</body>", once, StringComparison.Ordinal);
        var twice = WebIntegration.Transform(new TransformPayload { Contents = once });
        Assert.Equal(once, twice);
    }
}
