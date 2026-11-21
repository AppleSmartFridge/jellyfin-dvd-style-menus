using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.DiscMenus.Sessions;

/// <summary>
/// Short-lived pairing codes for the phone remote. A signed-in user creates a pairing code (shown as a
/// QR code and link). The phone claims it once and gets a remote token bound to that user, so the
/// phone never needs the user's Jellyfin password. Tokens live in memory only.
/// </summary>
public sealed class PairingService
{
    private static readonly TimeSpan PairLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemoteLifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, (Guid UserId, DateTime Expires)> _pairCodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Guid UserId, DateTime Expires)> _remoteTokens = new(StringComparer.Ordinal);

    public (string Code, DateTime Expires) CreatePairCode(Guid userId)
    {
        Prune();
        var code = NewToken(12);
        var expires = DateTime.UtcNow + PairLifetime;
        _pairCodes[code] = (userId, expires);
        return (code, expires);
    }

    public bool IsValidPairCode(string? code)
        => code != null && _pairCodes.TryGetValue(code, out var p) && p.Expires > DateTime.UtcNow;

    /// <summary>Exchanges a pairing code for a remote token. The code can only be used once.</summary>
    public (string Token, Guid UserId)? Claim(string? code)
    {
        if (code == null || !_pairCodes.TryRemove(code, out var p) || p.Expires <= DateTime.UtcNow)
        {
            return null;
        }

        var token = NewToken(32);
        _remoteTokens[token] = (p.UserId, DateTime.UtcNow + RemoteLifetime);
        return (token, p.UserId);
    }

    public Guid? ValidateRemoteToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_remoteTokens.TryGetValue(token, out var t))
        {
            return null;
        }

        if (t.Expires <= DateTime.UtcNow)
        {
            _remoteTokens.TryRemove(token, out _);
            return null;
        }

        return t.UserId;
    }

    private static string NewToken(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _pairCodes.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
        {
            _pairCodes.TryRemove(k, out _);
        }

        foreach (var k in _remoteTokens.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
        {
            _remoteTokens.TryRemove(k, out _);
        }
    }
}
