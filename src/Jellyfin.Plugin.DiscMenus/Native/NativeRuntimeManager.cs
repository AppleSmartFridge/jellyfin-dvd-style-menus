using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscMenus.Native;

/// <summary>Where a usable native runtime lives and how to start the helper from it.</summary>
public sealed class NativeRuntime
{
    public string Root { get; init; } = string.Empty;

    public string HelperPath { get; init; } = string.Empty;

    public string? JavaHome { get; init; }

    public string? BdjJar { get; init; }

    public string Source { get; init; } = string.Empty;

    /// <summary>Gets the environment variables the helper needs (library paths, Java, BD-J jar).</summary>
    public IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var lib = Path.Combine(Root, "lib");
        var bin = Path.Combine(Root, "bin");
        if (OperatingSystem.IsWindows())
        {
            env["PATH"] = bin + Path.PathSeparator + lib + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        else if (OperatingSystem.IsMacOS())
        {
            env["DYLD_LIBRARY_PATH"] = lib + Path.PathSeparator + Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH");
        }
        else
        {
            env["LD_LIBRARY_PATH"] = lib + Path.PathSeparator + Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        }

        if (JavaHome != null)
        {
            env["JAVA_HOME"] = JavaHome;
        }

        if (BdjJar != null)
        {
            env["LIBBLURAY_CP"] = BdjJar;
        }

        var fontsConf = Path.Combine(Root, "etc", "fonts", "fonts.conf");
        if (File.Exists(fontsConf))
        {
            env["FONTCONFIG_FILE"] = fontsConf;
            env["FONTCONFIG_PATH"] = Path.GetDirectoryName(fontsConf)!;
        }

        return env;
    }
}

/// <summary>
/// Finds, downloads and checks the platform runtime bundle (bdmenu helper, libbluray with BD-J,
/// FFmpeg libraries and a minimal Java runtime). The plugin needs nothing installed on the host.
/// </summary>
public sealed class NativeRuntimeManager
{
    private readonly IApplicationPaths _paths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NativeRuntimeManager> _logger;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly Lazy<RuntimeManifest> _manifest = new(LoadManifest);

    public NativeRuntimeManager(IApplicationPaths paths, IHttpClientFactory httpClientFactory, ILogger<NativeRuntimeManager> logger)
    {
        _paths = paths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Gets the last install or detection error, for the dashboard.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gets the runtime identifier for this host, e.g. linux-x64, linux-musl-arm64, win-x64, osx-arm64.</summary>
    public static string CurrentRid
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                var a => a.ToString().ToLowerInvariant(),
            };
            if (OperatingSystem.IsWindows())
            {
                return "win-" + arch;
            }

            if (OperatingSystem.IsMacOS())
            {
                return "osx-" + arch;
            }

            var musl = File.Exists("/etc/alpine-release") || Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
            return (musl ? "linux-musl-" : "linux-") + arch;
        }
    }

    public string RuntimeVersion => _manifest.Value.Version;

    private string InstallRoot => Path.Combine(_paths.PluginConfigurationsPath, "DiscMenus", "runtime", RuntimeVersion, CurrentRid);

    /// <summary>Finds an installed runtime without downloading anything.</summary>
    public NativeRuntime? TryGetInstalled()
    {
        var config = Plugin.Instance?.Configuration;
        var candidates = new List<(string Path, string Source)>();
        if (!string.IsNullOrWhiteSpace(config?.RuntimePathOverride))
        {
            candidates.Add((config.RuntimePathOverride, "settings override"));
        }

        var pluginDir = Path.GetDirectoryName(typeof(NativeRuntimeManager).Assembly.Location);
        if (pluginDir != null)
        {
            candidates.Add((Path.Combine(pluginDir, "runtime", CurrentRid), "bundled with plugin"));
        }

        candidates.Add((InstallRoot, "downloaded"));

        foreach (var (root, source) in candidates)
        {
            var rt = Inspect(root, source);
            if (rt != null)
            {
                return rt;
            }
        }

        return null;
    }

    /// <summary>Returns a usable runtime, downloading it first if allowed.</summary>
    public async Task<NativeRuntime> EnsureAsync(CancellationToken cancellationToken)
    {
        var existing = TryGetInstalled();
        if (existing != null)
        {
            return existing;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is { AutoDownloadRuntime: false })
        {
            throw new InvalidOperationException("The Disc Menus runtime is not installed and automatic download is turned off. Install it from the plugin settings page.");
        }

        await InstallAsync(cancellationToken).ConfigureAwait(false);
        return TryGetInstalled() ?? throw new InvalidOperationException("The runtime was downloaded but could not be found afterwards. " + LastError);
    }

    /// <summary>Downloads, checks and unpacks the runtime bundle for this host.</summary>
    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Inspect(InstallRoot, "downloaded") != null)
            {
                return;
            }

            var manifest = _manifest.Value;
            var rid = CurrentRid;
            if (!manifest.Bundles.TryGetValue(rid, out var bundle))
            {
                throw Fail($"No Disc Menus runtime is published for this platform ({rid}).");
            }

            var baseUrl = Plugin.Instance?.Configuration.RuntimeBaseUrlOverride;
            var usingOverride = !string.IsNullOrWhiteSpace(baseUrl);
            if (!usingOverride)
            {
                baseUrl = manifest.BaseUrl;
            }

            if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("{owner}", StringComparison.Ordinal))
            {
                throw Fail("This build has no runtime download address. Set one in the plugin settings, or use the offline plugin package.");
            }

            if (string.IsNullOrWhiteSpace(bundle.Sha256) && !usingOverride)
            {
                throw Fail("This build has no checksum for the runtime bundle, so it will not download it.");
            }

            var url = baseUrl.TrimEnd('/') + "/" + bundle.File;
            var tempDir = Path.Combine(_paths.TempDirectory, "discmenus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, bundle.File);
            try
            {
                _logger.LogInformation("Downloading Disc Menus runtime {Rid} from {Url}", rid, url);
                using (var client = _httpClientFactory.CreateClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(30);
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(zipPath);
                    await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                string actual;
                await using (var file = File.OpenRead(zipPath))
                {
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
                }

                if (!string.IsNullOrWhiteSpace(bundle.Sha256) && !actual.Equals(bundle.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw Fail($"Checksum mismatch for {bundle.File}: expected {bundle.Sha256}, got {actual}. The download was rejected.");
                }

                var staging = Path.Combine(tempDir, "unpacked");
                ZipFile.ExtractToDirectory(zipPath, staging);
                MakeExecutable(staging);

                if (Directory.Exists(InstallRoot))
                {
                    Directory.Delete(InstallRoot, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(InstallRoot)!);
                Directory.Move(staging, InstallRoot);
                LastError = null;
                _logger.LogInformation("Disc Menus runtime installed to {Path}", InstallRoot);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    private Exception Fail(string message)
    {
        LastError = message;
        _logger.LogError("{Message}", message);
        return new InvalidOperationException(message);
    }

    private static NativeRuntime? Inspect(string root, string source)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        var exe = OperatingSystem.IsWindows() ? "bdmenu.exe" : "bdmenu";
        var helper = Path.Combine(root, "bin", exe);
        if (!File.Exists(helper))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if ((File.GetUnixFileMode(helper) & UnixFileMode.UserExecute) == 0)
                {
                    MakeExecutable(root); // plugin zips do not always keep the executable bit
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only location: starting the helper will report the problem.
            }
        }

        var jre = Path.Combine(root, "jre");
        var jar = Directory.Exists(Path.Combine(root, "share", "java"))
            ? Directory.EnumerateFiles(Path.Combine(root, "share", "java"), "libbluray*.jar").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;

        return new NativeRuntime
        {
            Root = root,
            HelperPath = helper,
            JavaHome = Directory.Exists(jre) ? jre : null,
            BdjJar = jar,
            Source = source,
        };
    }

    private static void MakeExecutable(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dirs = new[] { Path.Combine(root, "bin"), Path.Combine(root, "jre", "bin"), Path.Combine(root, "jre", "lib") };
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (dir.EndsWith("lib", StringComparison.Ordinal) && name != "jspawnhelper")
                {
                    continue;
                }

                File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
    }

    private static RuntimeManifest LoadManifest()
    {
        var asm = typeof(NativeRuntimeManager).Assembly;
        using var s = asm.GetManifestResourceStream("Jellyfin.Plugin.DiscMenus.Native.runtime-manifest.json");
        if (s == null)
        {
            return new RuntimeManifest();
        }

        return JsonSerializer.Deserialize<RuntimeManifest>(s) ?? new RuntimeManifest();
    }

    private sealed class RuntimeManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "0.0.0";

        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("bundles")]
        public Dictionary<string, RuntimeBundle> Bundles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RuntimeBundle
    {
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
