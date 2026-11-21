using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.DiscMenus.Media;

/// <summary>
/// Builds the local URLs that Jellyfin's own ffmpeg reads from. They are anonymous, so title URLs carry
/// an HMAC signature and live URLs use an unguessable session id.
/// </summary>
public sealed class StreamUrls
{
    private readonly IServerApplicationHost _appHost;

    public StreamUrls(IServerApplicationHost appHost)
    {
        _appHost = appHost;
    }

    public string LocalBase => _appHost.GetApiUrlForLocalAccess(null!, false).TrimEnd('/');

    public string LiveUrl(string sessionId) => $"{LocalBase}/DiscMenus/Live/{sessionId}/stream.ts";

    public string TitleUrl(Guid itemId, int playlist)
    {
        var item = itemId.ToString("N");
        return $"{LocalBase}/DiscMenus/Titles/{item}/{playlist.ToString(CultureInfo.InvariantCulture)}/stream.m2ts?token={Sign(item, playlist)}";
    }

    public static bool Verify(Guid itemId, int playlist, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(itemId.ToString("N"), playlist));
        var actual = Encoding.ASCII.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Sign(string item, int playlist)
    {
        var key = Encoding.UTF8.GetBytes(GetSecret());
        var data = Encoding.UTF8.GetBytes(item + ":" + playlist.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(HMACSHA256.HashData(key, data), 0, 16).ToLowerInvariant();
    }

    private static string GetSecret()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return "discmenus";
        }

        if (string.IsNullOrEmpty(plugin.Configuration.StreamSecret))
        {
            plugin.Configuration.StreamSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            plugin.SaveConfiguration();
        }

        return plugin.Configuration.StreamSecret;
    }
}
