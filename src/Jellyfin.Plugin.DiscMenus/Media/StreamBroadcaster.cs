using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DiscMenus.Media;

/// <summary>
/// Copies a live MPEG-TS stream (the helper's stdout) to any number of readers. A new reader starts at
/// the current position. Chunks are kept aligned to 188-byte TS packets so readers can sync right away.
/// A reader that falls too far behind is dropped rather than slowing everyone down.
/// </summary>
public sealed class StreamBroadcaster : IDisposable
{
    private const int PacketSize = 188;
    private const int ChunkPackets = 348; // about 64 KB
    private const int MaxQueuedChunks = 512; // about 32 MB per reader

    private readonly List<Channel<byte[]>> _readers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;
    private bool _completed;

    /// <summary>Raised when data arrives (used for idle tracking).</summary>
    public event EventHandler? DataReceived;

    /// <summary>Raised once when the source stream ends.</summary>
    public event EventHandler? Completed;

    public int ReaderCount
    {
        get
        {
            lock (_lock)
            {
                return _readers.Count;
            }
        }
    }

    public long TotalBytes { get; private set; }

    public void Start(Stream source)
    {
        _pump = Task.Run(() => PumpAsync(source, _cts.Token));
    }

    /// <summary>Creates a reader stream that starts at the current live position.</summary>
    public Stream Subscribe()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedChunks)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        lock (_lock)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _readers.Add(channel);
            }
        }

        return new ReaderStream(this, channel);
    }

    public void Dispose()
    {
        _cts.Cancel();
        CompleteAll();
        _cts.Dispose();
    }

    private async Task PumpAsync(Stream source, CancellationToken token)
    {
        var buffer = new byte[PacketSize * ChunkPackets];
        var filled = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var n = await source.ReadAsync(buffer.AsMemory(filled), token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                filled += n;
                TotalBytes += n;
                var aligned = filled - (filled % PacketSize);
                if (aligned == 0)
                {
                    continue;
                }

                var chunk = buffer.AsSpan(0, aligned).ToArray();
                Broadcast(chunk);
                var rest = filled - aligned;
                if (rest > 0)
                {
                    Buffer.BlockCopy(buffer, aligned, buffer, 0, rest);
                }

                filled = rest;
                DataReceived?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            CompleteAll();
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Broadcast(byte[] chunk)
    {
        lock (_lock)
        {
            for (var i = _readers.Count - 1; i >= 0; i--)
            {
                if (!_readers[i].Writer.TryWrite(chunk))
                {
                    // Too slow: drop this reader. Jellyfin's ffmpeg will see the end and the session can reconnect.
                    _readers[i].Writer.TryComplete();
                    _readers.RemoveAt(i);
                }
            }
        }
    }

    private void CompleteAll()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (var r in _readers)
            {
                r.Writer.TryComplete();
            }

            _readers.Clear();
        }
    }

    private void Remove(Channel<byte[]> channel)
    {
        lock (_lock)
        {
            _readers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    private sealed class ReaderStream : Stream
    {
        private readonly StreamBroadcaster _owner;
        private readonly Channel<byte[]> _channel;
        private byte[]? _current;
        private int _offset;

        public ReaderStream(StreamBroadcaster owner, Channel<byte[]> channel)
        {
            _owner = owner;
            _channel = channel;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current == null || _offset >= _current.Length)
            {
                try
                {
                    _current = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var take = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owner.Remove(_channel);
            }

            base.Dispose(disposing);
        }
    }
}
