using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DiscMenus.Disc;

namespace Jellyfin.Plugin.DiscMenus.Media;

/// <summary>
/// Read-only, seekable stream that joins byte ranges from one or more files on disk.
/// Used to serve a playlist's clips (from a folder or from inside an ISO) as one m2ts file
/// with HTTP range support, so Jellyfin's ffmpeg can seek in it.
/// </summary>
public sealed class VirtualConcatStream : Stream
{
    private readonly FileExtent[] _extents;
    private readonly long[] _starts;
    private readonly Dictionary<string, FileStream> _open = new(StringComparer.Ordinal);
    private long _position;

    public VirtualConcatStream(IEnumerable<FileExtent> extents)
    {
        _extents = extents.Where(e => e.Length > 0).ToArray();
        _starts = new long[_extents.Length];
        long total = 0;
        for (var i = 0; i < _extents.Length; i++)
        {
            _starts[i] = total;
            total += _extents[i].Length;
        }

        Length = total;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.Length == 0)
        {
            return 0;
        }

        var index = Array.BinarySearch(_starts, _position);
        if (index < 0)
        {
            index = ~index - 1;
        }

        var extent = _extents[index];
        var within = _position - _starts[index];
        var take = (int)Math.Min(buffer.Length, extent.Length - within);
        var fs = GetStream(extent.SourcePath);
        fs.Position = extent.Offset + within;
        var read = fs.Read(buffer[..take]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var fs in _open.Values)
            {
                fs.Dispose();
            }

            _open.Clear();
        }

        base.Dispose(disposing);
    }

    private FileStream GetStream(string path)
    {
        if (!_open.TryGetValue(path, out var fs))
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            _open[path] = fs;
        }

        return fs;
    }
}
