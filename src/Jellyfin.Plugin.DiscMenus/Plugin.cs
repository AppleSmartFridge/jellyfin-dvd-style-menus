using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DiscMenus.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DiscMenus;

/// <summary>
/// Disc Menus plugin: plays decrypted Blu-ray ISOs and BDMV folders with HDMV and BD-J menus
/// that run on the server, so every Jellyfin client can show them.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin id. Keep this the same across releases.</summary>
    public static readonly Guid PluginGuid = new("ad6c53d6-78ce-4d7f-bda4-595ac69bf03d");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Disc Menus";

    public override Guid Id => PluginGuid;

    public override string Description =>
        "Plays decrypted Blu-ray ISOs and BDMV folders with HDMV and BD-J menus, rendered on the server.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "DiscMenus",
                DisplayName = "Disc Menus",
                EmbeddedResourcePath = ns + ".Configuration.configPage.html",
            },
            new PluginPageInfo
            {
                Name = "DiscRemote",
                DisplayName = "Disc Remote",
                EmbeddedResourcePath = ns + ".Web.dashboardRemote.html",
                EnableInMainMenu = true,
                MenuIcon = "settings_remote",
            },
        };
    }
}
