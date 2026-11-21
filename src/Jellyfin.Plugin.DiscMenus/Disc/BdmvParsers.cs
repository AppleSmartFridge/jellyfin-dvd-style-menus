using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.DiscMenus.Disc;

/// <summary>Object type of a disc title in index.bdmv.</summary>
public enum TitleObjectType
{
    Unknown = 0,
    Hdmv = 1,
    Bdj = 2,
}

/// <summary>Parsed index.bdmv.</summary>
public sealed class DiscIndex
{
    public string Version { get; init; } = string.Empty;

    public TitleObjectType FirstPlayback { get; init; }

    public TitleObjectType TopMenu { get; init; }

    public IReadOnlyList<TitleObjectType> Titles { get; init; } = Array.Empty<TitleObjectType>();

    public bool UsesBdj => FirstPlayback == TitleObjectType.Bdj || TopMenu == TitleObjectType.Bdj || Titles.Contains(TitleObjectType.Bdj);

    public bool HasHdmv => FirstPlayback == TitleObjectType.Hdmv || TopMenu == TitleObjectType.Hdmv || Titles.Contains(TitleObjectType.Hdmv);
}

/// <summary>One elementary stream listed in a playlist's STN table.</summary>
public sealed record PlaylistStream(StreamKind Kind, int Pid, int CodingType, string Language, int VideoFormat, int Rate);

/// <summary>Kind of elementary stream.</summary>
public enum StreamKind
{
    Video,
    Audio,
    PresentationGraphics,
    InteractiveGraphics,
    TextSubtitle,
}

/// <summary>One play item: a clip and the part of it that plays.</summary>
public sealed record PlayItem(string ClipName, uint InTime, uint OutTime, bool IsMultiAngle)
{
    /// <summary>Gets the duration in seconds (times are in 45 kHz units).</summary>
    public double DurationSeconds => OutTime > InTime ? (OutTime - InTime) / 45000.0 : 0;
}

/// <summary>Parsed .mpls playlist.</summary>
public sealed class Playlist
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<PlayItem> Items { get; init; } = Array.Empty<PlayItem>();

    /// <summary>Gets the streams from the first play item's STN table.</summary>
    public IReadOnlyList<PlaylistStream> Streams { get; init; } = Array.Empty<PlaylistStream>();

    /// <summary>Gets the chapter start times in seconds from the start of the playlist.</summary>
    public IReadOnlyList<double> Chapters { get; init; } = Array.Empty<double>();

    public double DurationSeconds
    {
        get
        {
            double total = 0;
            foreach (var i in Items)
            {
                total += i.DurationSeconds;
            }

            return total;
        }
    }

    /// <summary>Gets the playlist number (00800.mpls → 800).</summary>
    public int Number => int.TryParse(Path.GetFileNameWithoutExtension(Name), out var n) ? n : -1;
}

/// <summary>Parsers for index.bdmv and .mpls files.</summary>
public static class BdmvParsers
{
    public static DiscIndex ParseIndex(byte[] data)
    {
        if (data.Length < 40 || Encoding.ASCII.GetString(data, 0, 4) != "INDX")
        {
            throw new InvalidDataException("Not an index.bdmv file.");
        }

        var version = Encoding.ASCII.GetString(data, 4, 4);
        var indexesStart = (int)U32(data, 8);
        if (indexesStart + 4 + 12 + 12 + 2 > data.Length)
        {
            throw new InvalidDataException("index.bdmv truncated.");
        }

        var p = indexesStart + 4; // skip length
        var first = (TitleObjectType)(data[p] >> 6);
        p += 12;
        var top = (TitleObjectType)(data[p] >> 6);
        p += 12;
        var count = U16(data, p);
        p += 2;
        var titles = new List<TitleObjectType>(count);
        for (var i = 0; i < count && p + 12 <= data.Length; i++, p += 12)
        {
            titles.Add((TitleObjectType)(data[p] >> 6));
        }

        return new DiscIndex { Version = version, FirstPlayback = first, TopMenu = top, Titles = titles };
    }

    public static Playlist ParsePlaylist(string name, byte[] data)
    {
        if (data.Length < 40 || Encoding.ASCII.GetString(data, 0, 4) != "MPLS")
        {
            throw new InvalidDataException("Not an MPLS file: " + name);
        }

        var playlistStart = (int)U32(data, 8);
        var markStart = (int)U32(data, 12);

        var items = new List<PlayItem>();
        var streams = new List<PlaylistStream>();
        var p = playlistStart + 4 + 2; // length, reserved
        var itemCount = U16(data, p);
        p += 4; // item count + subpath count

        for (var i = 0; i < itemCount; i++)
        {
            var itemLength = U16(data, p);
            var itemStart = p + 2;
            var clip = Encoding.ASCII.GetString(data, itemStart, 5);
            var flags = U16(data, itemStart + 9);
            var multiAngle = (flags & 0x0010) != 0;
            var inTime = U32(data, itemStart + 12);
            var outTime = U32(data, itemStart + 16);
            items.Add(new PlayItem(clip, inTime, outTime, multiAngle));

            if (i == 0)
            {
                var q = itemStart + 20 + 8 + 1 + 1 + 2; // UO mask, random access, still mode, still time
                if (multiAngle)
                {
                    var angles = data[q];
                    q += 2 + ((angles - 1) * 10);
                }

                try
                {
                    ParseStn(data, q, streams);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    // Keep items even if the STN table is odd.
                }
            }

            p = itemStart + itemLength;
        }

        var chapters = new List<double>();
        if (markStart > 0 && markStart + 6 <= data.Length)
        {
            var count = U16(data, markStart + 4);
            var q = markStart + 6;
            for (var i = 0; i < count && q + 14 <= data.Length; i++, q += 14)
            {
                var type = data[q + 1];
                var itemRef = U16(data, q + 2);
                var ts = U32(data, q + 4);
                if (type != 1 || itemRef >= items.Count)
                {
                    continue; // only entry marks are chapters
                }

                double offset = 0;
                for (var k = 0; k < itemRef; k++)
                {
                    offset += items[k].DurationSeconds;
                }

                var within = ts >= items[itemRef].InTime ? (ts - items[itemRef].InTime) / 45000.0 : 0;
                var t = Math.Round(offset + within, 3);
                if (chapters.Count == 0 || t - chapters[^1] > 0.5)
                {
                    chapters.Add(t);
                }
            }
        }

        return new Playlist { Name = name, Items = items, Streams = streams, Chapters = chapters };
    }

    private static void ParseStn(byte[] data, int p, List<PlaylistStream> streams)
    {
        p += 2 + 2; // length, reserved
        int video = data[p], audio = data[p + 1], pg = data[p + 2], ig = data[p + 3];
        int secAudio = data[p + 4], secVideo = data[p + 5], pipPg = data[p + 6];
        p += 7 + 5;

        void Read(int count, StreamKind kind)
        {
            for (var i = 0; i < count; i++)
            {
                var entryLen = data[p];
                var entryType = data[p + 1];
                var pid = entryType switch
                {
                    1 => U16(data, p + 2),
                    2 => U16(data, p + 4),
                    3 or 4 => U16(data, p + 3),
                    _ => 0,
                };
                p += 1 + entryLen;

                var attrLen = data[p];
                var coding = data[p + 1];
                var lang = string.Empty;
                int format = 0, rate = 0;
                switch (coding)
                {
                    case 0x01 or 0x02 or 0x1B or 0xEA or 0x24:
                        format = data[p + 2] >> 4;
                        rate = data[p + 2] & 0x0F;
                        break;
                    case 0x03 or 0x04 or 0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xA1 or 0xA2:
                        format = data[p + 2] >> 4;
                        rate = data[p + 2] & 0x0F;
                        lang = Encoding.ASCII.GetString(data, p + 3, 3);
                        break;
                    case 0x90 or 0x91:
                        lang = Encoding.ASCII.GetString(data, p + 2, 3);
                        break;
                    case 0x92:
                        lang = Encoding.ASCII.GetString(data, p + 3, 3);
                        break;
                }

                p += 1 + attrLen;
                streams.Add(new PlaylistStream(kind, pid, coding, lang.Trim('\0', ' '), format, rate));
            }
        }

        Read(video, StreamKind.Video);
        Read(audio, StreamKind.Audio);
        Read(pg, StreamKind.PresentationGraphics);
        Read(ig, StreamKind.InteractiveGraphics);
        _ = secAudio;
        _ = secVideo;
        _ = pipPg;
    }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
}
