using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.DiscMenus.Tests;

/// <summary>Builds small synthetic BDMV files and folders for tests. No real disc content.</summary>
internal static class BdmvFixtures
{
    /// <summary>Builds index.bdmv. Object types: 1 = HDMV, 2 = BD-J.</summary>
    public static byte[] Index(int firstPlay, int topMenu, params int[] titles)
    {
        var body = new List<byte>();
        void Obj(int type)
        {
            body.Add((byte)(type << 6));
            body.AddRange(new byte[11]);
        }

        Obj(firstPlay);
        Obj(topMenu);
        body.Add((byte)(titles.Length >> 8));
        body.Add((byte)titles.Length);
        foreach (var t in titles)
        {
            Obj(t);
        }

        var data = new byte[40 + 4 + body.Count];
        Encoding.ASCII.GetBytes("INDX0200").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 40);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40), (uint)body.Count);
        body.CopyTo(data, 44);
        return data;
    }

    /// <summary>One play item: clip name, in/out in seconds.</summary>
    public sealed record Item(string Clip, double In, double Out);

    /// <summary>Builds an .mpls with the given items, one video + audio stream table, and chapter marks (seconds from start of item 0).</summary>
    public static byte[] Playlist(IReadOnlyList<Item> items, IReadOnlyList<(int ItemIndex, double Seconds)>? marks = null)
    {
        var list = new List<byte>();
        list.AddRange(new byte[4]); // length (patched later)
        list.AddRange(new byte[2]); // reserved
        list.Add((byte)(items.Count >> 8));
        list.Add((byte)items.Count);
        list.AddRange(new byte[2]); // subpaths

        foreach (var it in items)
        {
            var pi = new List<byte>();
            pi.AddRange(Encoding.ASCII.GetBytes(it.Clip));
            pi.AddRange(Encoding.ASCII.GetBytes("M2TS"));
            pi.AddRange(new byte[] { 0x00, 0x01 }); // not multi-angle, connection condition 1
            pi.Add(0); // STC id
            pi.AddRange(U32((uint)(it.In * 45000)));
            pi.AddRange(U32((uint)(it.Out * 45000)));
            pi.AddRange(new byte[8]); // UO mask
            pi.Add(0); // random access
            pi.Add(0); // still mode
            pi.AddRange(new byte[2]); // still time

            // STN table: 1 video (H.264 1080p 23.976), 1 audio (AC-3 eng)
            var stn = new List<byte>();
            stn.AddRange(new byte[2]); // reserved
            stn.AddRange(new byte[] { 1, 1, 0, 0, 0, 0, 0 });
            stn.AddRange(new byte[5]);
            stn.AddRange(new byte[] { 9, 1, 0x10, 0x11, 0, 0, 0, 0, 0, 0 }); // stream entry: len 9, type 1, pid 0x1011
            stn.AddRange(new byte[] { 5, 0x1B, 0x61, 0, 0, 0 });          // attrs: len 5, H.264, 1080p/23.976
            stn.AddRange(new byte[] { 9, 1, 0x11, 0x00, 0, 0, 0, 0, 0, 0 }); // pid 0x1100
            stn.AddRange(new byte[] { 5, 0x81, 0x31 });                    // AC-3, stereo/48k
            stn.AddRange(Encoding.ASCII.GetBytes("eng"));
            pi.Add((byte)(stn.Count >> 8));
            pi.Add((byte)stn.Count);
            pi.AddRange(stn);

            list.Add((byte)(pi.Count >> 8));
            list.Add((byte)pi.Count);
            list.AddRange(pi);
        }

        var marksBlock = new List<byte>();
        marksBlock.AddRange(new byte[4]);
        var m = marks ?? Array.Empty<(int, double)>();
        marksBlock.Add((byte)(m.Count >> 8));
        marksBlock.Add((byte)m.Count);
        foreach (var (itemIndex, seconds) in m)
        {
            marksBlock.Add(0);
            marksBlock.Add(1); // entry mark
            marksBlock.Add((byte)(itemIndex >> 8));
            marksBlock.Add((byte)itemIndex);
            marksBlock.AddRange(U32((uint)((items[itemIndex].In + seconds) * 45000)));
            marksBlock.AddRange(new byte[] { 0xFF, 0xFF });
            marksBlock.AddRange(new byte[4]);
        }

        var playlistStart = 40 + 18; // after header and a dummy AppInfo
        var markStart = playlistStart + list.Count;
        var data = new byte[markStart + marksBlock.Count];
        Encoding.ASCII.GetBytes("MPLS0200").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)playlistStart);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), (uint)markStart);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(playlistStart), (uint)(list.Count - 4));
        list.CopyTo(4, data, playlistStart + 4, list.Count - 4);
        marksBlock.CopyTo(data, markStart);
        return data;
    }

    /// <summary>Builds a fake m2ts: 192-byte source packets with TS sync bytes. Copy-permission bits set if encrypted.</summary>
    public static byte[] Clip(int packets, bool encrypted = false)
    {
        var data = new byte[packets * 192];
        for (var i = 0; i < packets; i++)
        {
            data[i * 192] = encrypted ? (byte)0xC0 : (byte)0;
            data[(i * 192) + 4] = 0x47;
        }

        return data;
    }

    /// <summary>Writes a disc folder: BDMV/index.bdmv, BDMV/PLAYLIST/*.mpls and BDMV/STREAM/*.m2ts.</summary>
    public static string WriteFolder(byte[] index, IReadOnlyDictionary<string, byte[]> playlists, IReadOnlyDictionary<string, byte[]> clips, bool withJar = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "dm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "BDMV", "PLAYLIST"));
        Directory.CreateDirectory(Path.Combine(root, "BDMV", "STREAM"));
        File.WriteAllBytes(Path.Combine(root, "BDMV", "index.bdmv"), index);
        foreach (var (name, bytes) in playlists)
        {
            File.WriteAllBytes(Path.Combine(root, "BDMV", "PLAYLIST", name), bytes);
        }

        foreach (var (name, bytes) in clips)
        {
            File.WriteAllBytes(Path.Combine(root, "BDMV", "STREAM", name), bytes);
        }

        if (withJar)
        {
            Directory.CreateDirectory(Path.Combine(root, "BDMV", "JAR"));
            File.WriteAllBytes(Path.Combine(root, "BDMV", "JAR", "00000.jar"), new byte[] { (byte)'P', (byte)'K', 3, 4 });
        }

        return root;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }
}
