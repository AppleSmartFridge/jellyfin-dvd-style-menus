using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DiscMenus.Configuration;

/// <summary>Plugin settings, changed on the dashboard page.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the "Disc Menu (Interactive)" version is offered.</summary>
    public bool EnableMenus { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether each disc playlist is offered as a playable version.</summary>
    public bool EnableTitleVersions { get; set; } = true;

    /// <summary>Gets or sets the minimum playlist length, in seconds, for it to be listed as a title version.</summary>
    public int MinTitleSeconds { get; set; } = 120;

    /// <summary>Gets or sets the largest number of title versions listed per disc (main feature included).</summary>
    public int MaxTitleVersions { get; set; } = 15;

    /// <summary>Gets or sets the number of seconds after the first menu appears with no input before Enter is pressed automatically (0 = off).</summary>
    public int AutoEnterSeconds { get; set; }

    /// <summary>Gets or sets the menu stream's video bitrate in kbit/s.</summary>
    public int MenuVideoBitrateKbps { get; set; } = 10000;

    /// <summary>Gets or sets the x264 preset (or nearest equivalent) for the menu stream.</summary>
    public string EncoderPreset { get; set; } = "veryfast";

    /// <summary>Gets or sets the preferred H.264 encoder (auto, libx264, h264_nvenc, h264_mf, h264_videotoolbox). If it cannot open, the helper falls back to the next one that works.</summary>
    public string PreferredEncoder { get; set; } = "auto";

    /// <summary>Gets or sets the player region (A, B or C) reported to discs.</summary>
    public string RegionCode { get; set; } = "A";

    /// <summary>Gets or sets the preferred menu/audio language (ISO 639-2).</summary>
    public string PreferredLanguage { get; set; } = "eng";

    /// <summary>Gets or sets the number of minutes a menu session may sit with no viewer and no input before it is stopped.</summary>
    public int IdleTimeoutMinutes { get; set; } = 10;

    /// <summary>Gets or sets the largest number of menu sessions that may run at once.</summary>
    public int MaxConcurrentSessions { get; set; } = 2;

    /// <summary>Gets or sets a value indicating whether the native runtime may be downloaded automatically.</summary>
    public bool AutoDownloadRuntime { get; set; } = true;

    /// <summary>Gets or sets an optional folder holding an unpacked native runtime (overrides the download).</summary>
    public string RuntimePathOverride { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional base URL to download runtime bundles from (overrides the built-in one).</summary>
    public string RuntimeBaseUrlOverride { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether jellyfin-web gets the Disc Remote OSD button and web remote.</summary>
    public bool EnableWebIntegration { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether jellyfin-web keyboard keys are sent to the disc menu during menu playback.</summary>
    public bool EnableWebKeyCapture { get; set; }

    /// <summary>Gets or sets the secret used to sign title stream URLs. Created on first use.</summary>
    public string StreamSecret { get; set; } = string.Empty;
}
