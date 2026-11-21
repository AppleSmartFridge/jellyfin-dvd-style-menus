using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.DiscMenus.Disc;

/// <summary>
/// Minimal read-only UDF reader (UDF 1.02 to 2.60, including the 2.50 metadata partition
/// that Blu-ray uses). It only lists directories and maps files to byte ranges in the ISO.
/// It is enough for BDMV structures and does not try to be a general UDF implementation.
/// </summary>
public sealed class UdfIsoFileSystem : IDiscFileSystem
{
    private const int SectorSize = 2048;
    private const ushort TagFileSetDescriptor = 256;
    private const ushort TagFileIdentifier = 257;
    private const ushort TagFileEntry = 261;
    private const ushort TagExtendedFileEntry = 266;

    private readonly FileStream _stream;
    private readonly object _lock = new();
    private readonly Dictionary<ushort, PartitionMap> _maps = new();
    private readonly Dictionary<ushort, (long Start, long Length)> _partitions = new();
    private readonly List<(long Lbn, long Count)> _metadataExtents = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private int _blockSize = SectorSize;
    private bool? _fileDataInPhysical;

    private UdfIsoFileSystem(string path)
    {
        SourcePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
    }

    private enum MapKind
    {
        Physical,
        Metadata,
    }

    public string SourcePath { get; }

    public bool IsIso => true;

    /// <summary>Opens an ISO. Returns null if it is not a readable UDF image.</summary>
    public static UdfIsoFileSystem? TryOpen(string path)
    {
        UdfIsoFileSystem? fs = null;
        try
        {
            fs = new UdfIsoFileSystem(path);
            fs.Mount();
            return fs;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            fs?.Dispose();
            return null;
        }
    }

    public bool FileExists(string relativePath) => _nodes.TryGetValue(Normalize(relativePath), out var n) && !n.IsDirectory;

    public bool DirectoryExists(string relativePath) => _nodes.TryGetValue(Normalize(relativePath), out var n) && n.IsDirectory;

    public IReadOnlyList<string> ListFiles(string relativeDirectory)
    {
        var prefix = Normalize(relativeDirectory);
        if (!_nodes.TryGetValue(prefix, out var dir) || !dir.IsDirectory)
        {
            return Array.Empty<string>();
        }

        return dir.Children.Where(c => !c.IsDirectory).Select(c => c.Name).ToList();
    }

    public long GetLength(string relativePath) => RequireFile(relativePath).Entry.Length;

    public IReadOnlyList<FileExtent> GetExtents(string relativePath)
    {
        var node = RequireFile(relativePath);
        if (node.Entry.Embedded != null)
        {
            throw new NotSupportedException("File is stored inline in its file entry; use ReadAllBytes: " + relativePath);
        }

        return ResolveFileExtents(node);
    }

    public byte[] ReadAllBytes(string relativePath)
    {
        var node = RequireFile(relativePath);
        if (node.Entry.Length > 64 * 1024 * 1024)
        {
            throw new InvalidOperationException("File too large to read whole: " + relativePath);
        }

        return ReadPrefix(relativePath, (int)node.Entry.Length);
    }

    public byte[] ReadPrefix(string relativePath, int count)
    {
        var node = RequireFile(relativePath);
        if (node.Entry.Embedded != null)
        {
            return node.Entry.Embedded.AsSpan(0, (int)Math.Min(count, node.Entry.Embedded.Length)).ToArray();
        }

        return ReadExtents(ResolveFileExtents(node), count);
    }

    public void Dispose() => _stream.Dispose();

    private static string Normalize(string p) => string.Join('/', p.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries));

    private Node RequireFile(string relativePath)
    {
        if (_nodes.TryGetValue(Normalize(relativePath), out var n) && !n.IsDirectory)
        {
            return n;
        }

        throw new FileNotFoundException("Not found in ISO: " + relativePath);
    }

    private byte[] ReadSector(long sector, int count = 1)
    {
        var buf = new byte[SectorSize * count];
        lock (_lock)
        {
            _stream.Position = sector * SectorSize;
            var read = 0;
            while (read < buf.Length)
            {
                var n = _stream.Read(buf, read, buf.Length - read);
                if (n == 0)
                {
                    throw new InvalidDataException("Unexpected end of ISO.");
                }

                read += n;
            }
        }

        return buf;
    }

    private static ushort TagId(byte[] b, int offset = 0) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));

    private void Mount()
    {
        // Anchor Volume Descriptor Pointer at sector 256.
        var avdp = ReadSector(256);
        if (TagId(avdp) != 2)
        {
            throw new InvalidDataException("No UDF anchor.");
        }

        var vdsLength = BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(16));
        var vdsLocation = BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(20));
        var vdsSectors = (int)Math.Min(64, Math.Max(1, vdsLength / SectorSize));

        (uint Lbn, ushort PartRef) fsdLocation = default;
        var haveLvd = false;
        for (var i = 0; i < vdsSectors; i++)
        {
            var d = ReadSector(vdsLocation + i);
            var tag = TagId(d);
            if (tag == 8)
            {
                break; // terminating descriptor
            }

            if (tag == 5)
            {
                var number = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(22));
                var start = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(188));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(192));
                _partitions.TryAdd(number, (start, length));
            }
            else if (tag == 6 && !haveLvd)
            {
                haveLvd = true;
                _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(212));
                if (_blockSize != SectorSize)
                {
                    throw new InvalidDataException("Unsupported UDF block size " + _blockSize);
                }

                fsdLocation = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(252)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(256)));
                var mapCount = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(268));
                var pos = 440;
                for (ushort m = 0; m < mapCount && pos + 2 <= d.Length; m++)
                {
                    var type = d[pos];
                    var len = d[pos + 1];
                    if (len == 0)
                    {
                        break;
                    }

                    if (type == 1)
                    {
                        _maps[m] = new PartitionMap(MapKind.Physical, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 4)), 0);
                    }
                    else if (type == 2)
                    {
                        var ident = Encoding.ASCII.GetString(d, pos + 5, 23).TrimEnd('\0', ' ');
                        var partNumber = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 38));
                        if (ident.StartsWith("*UDF Metadata Partition", StringComparison.Ordinal))
                        {
                            _maps[m] = new PartitionMap(MapKind.Metadata, partNumber, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 40)));
                        }
                        else
                        {
                            // Virtual / sparable partitions: treat as physical (enough for pressed-disc images).
                            _maps[m] = new PartitionMap(MapKind.Physical, partNumber, 0);
                        }
                    }

                    pos += len;
                }
            }
        }

        if (!haveLvd || _partitions.Count == 0)
        {
            throw new InvalidDataException("Missing UDF volume descriptors.");
        }

        if (_maps.Count == 0)
        {
            _maps[0] = new PartitionMap(MapKind.Physical, _partitions.Keys.First(), 0);
        }

        var meta = _maps.Values.FirstOrDefault(m => m.Kind == MapKind.Metadata);
        if (meta != null)
        {
            // Metadata file entry lives in the physical partition.
            var physRef = PhysicalRefFor(meta.PartitionNumber);
            var mfe = ReadFileEntry(meta.MetadataFileLocation, physRef);
            foreach (var ad in mfe.Extents)
            {
                _metadataExtents.Add((ad.Lbn, (ad.Length + SectorSize - 1) / SectorSize));
            }
        }

        var fsd = ReadBlock(fsdLocation.Lbn, fsdLocation.PartRef);
        if (TagId(fsd) != TagFileSetDescriptor)
        {
            throw new InvalidDataException("No UDF file set descriptor.");
        }

        var rootLbn = BinaryPrimitives.ReadUInt32LittleEndian(fsd.AsSpan(404));
        var rootRef = BinaryPrimitives.ReadUInt16LittleEndian(fsd.AsSpan(408));
        var rootEntry = ReadFileEntry(rootLbn, rootRef);
        var root = new Node(string.Empty, true, rootEntry);
        _nodes[string.Empty] = root;
        Walk(root, string.Empty, 0);
    }

    private ushort PhysicalRefFor(ushort partitionNumber)
    {
        foreach (var (key, map) in _maps)
        {
            if (map.Kind == MapKind.Physical && map.PartitionNumber == partitionNumber)
            {
                return key;
            }
        }

        return _maps.First(m => m.Value.Kind == MapKind.Physical).Key;
    }

    private void Walk(Node dir, string path, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        var data = ReadEntryData(dir.Entry);
        var pos = 0;
        while (pos + 38 <= data.Length)
        {
            if (TagId(data, pos) != TagFileIdentifier)
            {
                // Padding to the next block, or the end of the directory.
                var next = ((pos / SectorSize) + 1) * SectorSize;
                if (next <= pos || next >= data.Length)
                {
                    break;
                }

                pos = next;
                continue;
            }

            var characteristics = data[pos + 18];
            var lfi = data[pos + 19];
            var icbLbn = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 24));
            var icbRef = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 28));
            var liu = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 36));
            var nameStart = pos + 38 + liu;
            var total = (38 + liu + lfi + 3) & ~3;

            var isParent = (characteristics & 0x08) != 0;
            var isDeleted = (characteristics & 0x04) != 0;
            if (!isParent && !isDeleted && lfi > 0 && nameStart + lfi <= data.Length)
            {
                var name = DecodeName(data.AsSpan(nameStart, lfi));
                var isDir = (characteristics & 0x02) != 0;
                try
                {
                    var entry = ReadFileEntry(icbLbn, icbRef);
                    var child = new Node(name, isDir, entry);
                    dir.Children.Add(child);
                    var childPath = path.Length == 0 ? name : path + "/" + name;
                    _nodes[childPath] = child;
                    if (isDir)
                    {
                        Walk(child, childPath, depth + 1);
                    }
                }
                catch (InvalidDataException)
                {
                    // Skip unreadable entries; BDMV parsing will report what is missing.
                }
            }

            pos += total;
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 1)
        {
            return string.Empty;
        }

        return raw[0] switch
        {
            8 => Encoding.Latin1.GetString(raw[1..]),
            16 => Encoding.BigEndianUnicode.GetString(raw[1..]),
            _ => Encoding.Latin1.GetString(raw[1..]),
        };
    }

    /// <summary>Converts a partition-relative block to an absolute ISO sector.</summary>
    private long ToSector(long lbn, ushort partRef)
    {
        if (!_maps.TryGetValue(partRef, out var map))
        {
            map = _maps.Values.First();
        }

        if (map.Kind == MapKind.Metadata)
        {
            var remaining = lbn;
            foreach (var (extLbn, count) in _metadataExtents)
            {
                if (remaining < count)
                {
                    return ToSector(extLbn + remaining, PhysicalRefFor(map.PartitionNumber));
                }

                remaining -= count;
            }

            throw new InvalidDataException("Block outside metadata partition.");
        }

        if (!_partitions.TryGetValue(map.PartitionNumber, out var part))
        {
            part = _partitions.Values.First();
        }

        return part.Start + lbn;
    }

    private byte[] ReadBlock(long lbn, ushort partRef) => ReadSector(ToSector(lbn, partRef));

    private Entry ReadFileEntry(long lbn, ushort partRef)
    {
        var b = ReadBlock(lbn, partRef);
        var tag = TagId(b);
        int lEaOffset, adStartBase;
        if (tag == TagFileEntry)
        {
            lEaOffset = 168;
            adStartBase = 176;
        }
        else if (tag == TagExtendedFileEntry)
        {
            lEaOffset = 208;
            adStartBase = 216;
        }
        else
        {
            throw new InvalidDataException("Expected a file entry, found tag " + tag);
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(34));
        var allocType = flags & 7;
        var length = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(56));
        var lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(lEaOffset));
        var lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(lEaOffset + 4));
        var adStart = adStartBase + lEa;
        if (adStart + lAd > b.Length)
        {
            throw new InvalidDataException("File entry descriptors overflow the block.");
        }

        var entry = new Entry { Length = length, PartRef = partRef };
        switch (allocType)
        {
            case 0: // short_ad
                for (var p = adStart; p + 8 <= adStart + lAd; p += 8)
                {
                    var raw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
                    var len = raw & 0x3FFFFFFF;
                    var type = raw >> 30;
                    if (len == 0)
                    {
                        break;
                    }

                    entry.Extents.Add(new Ad(len, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4)), partRef, type, false));
                }

                break;
            case 1: // long_ad
                for (var p = adStart; p + 16 <= adStart + lAd; p += 16)
                {
                    var raw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
                    var len = raw & 0x3FFFFFFF;
                    var type = raw >> 30;
                    if (len == 0)
                    {
                        break;
                    }

                    entry.Extents.Add(new Ad(len, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 8)), type, true));
                }

                break;
            case 3: // data embedded in the entry
                entry.Embedded = b.AsSpan(adStart, (int)Math.Min(lAd, length)).ToArray();
                break;
            default:
                throw new InvalidDataException("Unsupported UDF allocation type " + allocType);
        }

        return entry;
    }

    private byte[] ReadEntryData(Entry entry)
    {
        if (entry.Embedded != null)
        {
            return entry.Embedded;
        }

        var extents = entry.Extents
            .Where(a => a.Type == 0)
            .Select(a => new FileExtent(SourcePath, ToSector(a.Lbn, a.PartRef) * SectorSize, a.Length))
            .ToList();
        return ReadExtents(extents, (int)Math.Min(entry.Length, 16 * 1024 * 1024));
    }

    private byte[] ReadExtents(IReadOnlyList<FileExtent> extents, int count)
    {
        var result = new byte[count];
        var written = 0;
        foreach (var e in extents)
        {
            if (written >= count)
            {
                break;
            }

            var take = (int)Math.Min(e.Length, count - written);
            lock (_lock)
            {
                _stream.Position = e.Offset;
                _stream.ReadExactly(result, written, take);
            }

            written += take;
        }

        return written == count ? result : result.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// Maps a file's allocation descriptors to ISO byte ranges.
    /// Authoring tools disagree on whether short_ads in file entries stored in the UDF 2.50 metadata
    /// partition point into the metadata partition or the physical partition. Real Blu-ray images put
    /// file data in the physical partition, so both readings are checked against the file's magic bytes
    /// and the first one that matches is kept for the whole volume.
    /// </summary>
    private IReadOnlyList<FileExtent> ResolveFileExtents(Node node)
    {
        var entry = node.Entry;
        var inMeta = _maps.TryGetValue(entry.PartRef, out var map) && map.Kind == MapKind.Metadata;
        if (!inMeta || entry.Extents.All(e => e.IsLong))
        {
            return BuildExtents(entry, physicalForShort: false);
        }

        if (_fileDataInPhysical is bool known)
        {
            return BuildExtents(entry, known);
        }

        foreach (var physical in new[] { true, false })
        {
            try
            {
                var ext = BuildExtents(entry, physical);
                if (LooksValid(node.Name, ReadExtents(ext, 8)))
                {
                    if (HasKnownMagic(node.Name))
                    {
                        _fileDataInPhysical = physical;
                    }

                    return ext;
                }
            }
            catch (InvalidDataException)
            {
                // Try the other reading.
            }
        }

        return BuildExtents(entry, physicalForShort: true);
    }

    private List<FileExtent> BuildExtents(Entry entry, bool physicalForShort)
    {
        var list = new List<FileExtent>();
        foreach (var a in entry.Extents)
        {
            if (a.Type != 0)
            {
                continue; // allocated-but-unrecorded or continuation: not used by BDMV files
            }

            var partRef = a.PartRef;
            if (!a.IsLong && physicalForShort && _maps.TryGetValue(partRef, out var m) && m.Kind == MapKind.Metadata)
            {
                partRef = PhysicalRefFor(m.PartitionNumber);
            }

            list.Add(new FileExtent(SourcePath, ToSector(a.Lbn, partRef) * SectorSize, a.Length));
        }

        // Trim the last extent to the file length.
        var total = list.Sum(e => e.Length);
        if (total > entry.Length && list.Count > 0)
        {
            var last = list[^1];
            list[^1] = last with { Length = Math.Max(0, last.Length - (total - entry.Length)) };
        }

        return list;
    }

    private static bool HasKnownMagic(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".mpls" or ".clpi" or ".bdmv" or ".m2ts";
    }

    private static bool LooksValid(string name, byte[] head)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (head.Length < 5)
        {
            return false;
        }

        var magic = Encoding.ASCII.GetString(head, 0, 4);
        return ext switch
        {
            ".mpls" => magic == "MPLS",
            ".clpi" => magic == "HDMV",
            ".bdmv" => magic is "INDX" or "MOBJ",
            ".m2ts" => head[4] == 0x47,
            ".jar" => head[0] == (byte)'P' && head[1] == (byte)'K',
            _ => true,
        };
    }

    private sealed record PartitionMap(MapKind Kind, ushort PartitionNumber, uint MetadataFileLocation);

    private sealed record Ad(long Length, long Lbn, ushort PartRef, uint Type, bool IsLong);

    private sealed class Entry
    {
        public long Length { get; set; }

        public ushort PartRef { get; set; }

        public List<Ad> Extents { get; } = new();

        public byte[]? Embedded { get; set; }
    }

    private sealed class Node
    {
        public Node(string name, bool isDirectory, Entry entry)
        {
            Name = name;
            IsDirectory = isDirectory;
            Entry = entry;
        }

        public string Name { get; }

        public bool IsDirectory { get; }

        public Entry Entry { get; }

        public List<Node> Children { get; } = new();
    }
}
