using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscMenus.Media;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus.Sessions;

/// <summary>One running disc menu: a bdmenu helper process plus its output stream and state.</summary>
public sealed class MenuSession : IDisposable
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "up", "down", "left", "right", "enter", "menu", "popup", "back",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "red", "green", "yellow", "blue", "play", "pause", "stop", "next", "prev",
    };

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private readonly List<Channel<string>> _listeners = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private JsonObject _state = new();
    private int _disposed;

    internal MenuSession(string id, Guid itemId, string itemName, string menuKind, Process process, ILogger logger)
    {
        Id = id;
        ItemId = itemId;
        ItemName = itemName;
        MenuKind = menuKind;
        _process = process;
        _logger = logger;
        Created = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
        Broadcaster = new StreamBroadcaster();
        Broadcaster.DataReceived += (_, _) => LastData = DateTime.UtcNow;
        Broadcaster.Completed += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the helper's output ends (process exit or crash).</summary>
    public event EventHandler? Exited;

    public string Id { get; }

    public Guid ItemId { get; }

    public string ItemName { get; }

    public string MenuKind { get; }

    public DateTime Created { get; }

    /// <summary>Gets or sets the last time a viewer or remote touched the session.</summary>
    public DateTime LastActivity { get; set; }

    public DateTime LastData { get; private set; }

    /// <summary>Gets or sets the user that started playback, once known from the Jellyfin session list.</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>Gets or sets the Jellyfin device ids currently playing this session.</summary>
    public IReadOnlyList<string> DeviceIds { get; set; } = Array.Empty<string>();

    public StreamBroadcaster Broadcaster { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Gets a copy of the current disc state reported by the helper.</summary>
    public JsonObject GetState()
    {
        lock (_stateLock)
        {
            return (JsonObject)_state.DeepClone();
        }
    }

    internal void Start()
    {
        Broadcaster.Start(_process.StandardOutput.BaseStream);
        _ = Task.Run(ReadStderrAsync);
    }

    /// <summary>Sends a remote key to the disc. Returns false for unknown keys.</summary>
    public Task<bool> SendKeyAsync(string key)
    {
        if (!AllowedKeys.Contains(key))
        {
            return Task.FromResult(false);
        }

        return SendLineAsync("key " + key.ToLowerInvariant());
    }

    /// <summary>Sends a mouse click. Coordinates are 0..1 across the picture.</summary>
    public Task<bool> SendMouseAsync(double x, double y, bool click)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        return SendLineAsync(FormattableString.Invariant($"{(click ? "click" : "move")} {x:0.0000} {y:0.0000}"));
    }

    /// <summary>Sends a validated helper command (title, audio, subtitle, chapter).</summary>
    public Task<bool> SendCommandAsync(string command, int value)
    {
        var line = command.ToLowerInvariant() switch
        {
            "title" when value >= 0 && value < 65535 => "title " + value,
            "audio" when value > 0 && value < 33 => "audio " + value,
            "subtitle" when value >= 0 && value < 33 => "pg " + value,
            "chapter" when value is -1 or 1 => "chapter " + (value > 0 ? "+1" : "-1"),
            _ => null,
        };
        return line == null ? Task.FromResult(false) : SendLineAsync(line);
    }

    /// <summary>Subscribes to state updates (JSON strings). Dispose the reader by completing it.</summary>
    public ChannelReader<string> SubscribeState(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_stateLock)
        {
            _listeners.Add(channel);
            channel.Writer.TryWrite(_state.ToJsonString());
        }

        cancellationToken.Register(() =>
        {
            lock (_stateLock)
            {
                _listeners.Remove(channel);
            }

            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.StandardInput.WriteLine("quit");
                    _process.StandardInput.Flush();
                }
                catch (IOException)
                {
                }

                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(true);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        Broadcaster.Dispose();
        lock (_stateLock)
        {
            foreach (var l in _listeners)
            {
                l.Writer.TryComplete();
            }

            _listeners.Clear();
        }

        _process.Dispose();
    }

    private async Task<bool> SendLineAsync(string line)
    {
        if (HasExited)
        {
            return false;
        }

        LastActivity = DateTime.UtcNow;
        await _stdinLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.StartsWith("@evt ", StringComparison.Ordinal))
                {
                    ApplyEvent(line.AsSpan(5).ToString());
                }
                else if (line.Length > 0)
                {
                    _logger.LogDebug("bdmenu[{Id}]: {Line}", Id, line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void ApplyEvent(string json)
    {
        JsonObject? ev;
        try
        {
            ev = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (ev == null)
        {
            return;
        }

        if (ev["error"] is JsonNode err)
        {
            _logger.LogWarning("bdmenu[{Id}] reported: {Error}", Id, err.ToJsonString());
        }

        string snapshot;
        lock (_stateLock)
        {
            foreach (var (key, value) in ev)
            {
                _state[key] = value?.DeepClone();
            }

            snapshot = _state.ToJsonString();
            foreach (var l in _listeners)
            {
                l.Writer.TryWrite(snapshot);
            }
        }
    }
}
