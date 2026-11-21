using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus.Web;

/// <summary>Payload handed to the transform callback by the File Transformation plugin.</summary>
public sealed class TransformPayload
{
    public string? Contents { get; set; }
}

/// <summary>
/// Adds the Disc Menus script to jellyfin-web's index.html through the community File Transformation
/// plugin, when it is installed. The transformation happens in memory as the page is served; no file on
/// disk is changed. Without File Transformation, the script can be added with any script-injector plugin.
/// </summary>
public sealed class WebIntegration : IHostedService
{
    internal const string ScriptTag = "<script defer src=\"../DiscMenus/web-inject.js\"></script>";

    private static readonly Guid TransformId = new("4f6b9a43-2d55-4a59-a3ff-6b1d1f1a6c21");
    private readonly ILogger<WebIntegration> _logger;

    public WebIntegration(ILogger<WebIntegration> logger)
    {
        _logger = logger;
    }

    /// <summary>Callback used by File Transformation. Must stay public and static.</summary>
    public static string Transform(TransformPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        if (html.Contains("DiscMenus/web-inject.js", StringComparison.Ordinal))
        {
            return html;
        }

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? html + ScriptTag : html.Insert(idx, ScriptTag);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Register();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not register the Disc Menus web script with File Transformation");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Register()
    {
        var assemblies = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).ToList();
        var ft = assemblies.FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
        if (ft == null)
        {
            _logger.LogInformation("File Transformation plugin not found. The Disc Remote OSD button needs it (or a script injector) in jellyfin-web; the Disc Remote menu page and phone remote work without it");
            return;
        }

        var iface = ft.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        var register = iface?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
        if (register == null)
        {
            _logger.LogWarning("File Transformation is installed but its RegisterTransformation method was not found");
            return;
        }

        var paramType = register.GetParameters().FirstOrDefault()?.ParameterType;
        var json = "{"
            + "\"id\":\"" + TransformId + "\","
            + "\"fileNamePattern\":\"index.html\","
            + "\"callbackAssembly\":\"" + typeof(WebIntegration).Assembly.FullName + "\","
            + "\"callbackClass\":\"" + typeof(WebIntegration).FullName + "\","
            + "\"callbackMethod\":\"" + nameof(Transform) + "\""
            + "}";

        // RegisterTransformation takes a Newtonsoft JObject; build it by reflection so this plugin does not
        // depend on Newtonsoft at compile time.
        var parse = paramType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        if (parse == null)
        {
            _logger.LogWarning("Unexpected File Transformation API ({Type})", paramType);
            return;
        }

        register.Invoke(null, new[] { parse.Invoke(null, new object[] { json }) });
        _logger.LogInformation("Registered the Disc Menus web script with File Transformation");
    }
}
