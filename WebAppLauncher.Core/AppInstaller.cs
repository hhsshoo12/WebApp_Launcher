namespace WebAppLauncher.Core;

public sealed class AppInstaller
{
    private readonly WebAppPaths paths;
    private readonly ToolResolver tools;
    private readonly AppRepository repository;
    private readonly PortManager ports;

    public AppInstaller(WebAppPaths paths)
    {
        this.paths = paths;
        tools = new ToolResolver(paths);
        repository = new AppRepository(paths);
        ports = new PortManager();
    }

    public async Task<InstalledApp> InstallAsync(string wapkPath, CancellationToken cancellationToken = default)
    {
        paths.EnsureRootLayout();
        var wapk = TomlManifestStore.LoadWapk(wapkPath);
        var installDirectory = paths.GetAppDirectory(wapk.Package.Id, wapk.Package.Version);
        var sourceDirectory = Path.Combine(installDirectory, "source");

        if (Directory.Exists(installDirectory))
        {
            throw new InvalidOperationException($"App version is already installed: {wapk.Package.Id}/{wapk.Package.Version}");
        }

        try
        {
            Directory.CreateDirectory(installDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(Path.Combine(installDirectory, "data"));
            Directory.CreateDirectory(Path.Combine(installDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(installDirectory, "temp"));

            var checkoutDirectory = await CheckoutAsync(wapk, cancellationToken);
            var appSource = SafeCombine(checkoutDirectory, wapk.Source.AppDir);
            CopyDirectory(appSource, sourceDirectory);
            File.Copy(wapkPath, Path.Combine(sourceDirectory, "app.wapk"), overwrite: true);

            var htmlPath = Path.Combine(sourceDirectory, wapk.Entry.Html);
            if (!File.Exists(htmlPath))
            {
                throw new InvalidDataException($"Missing required app.html entry: {htmlPath}");
            }

            var mode = ResolveMode(wapk, sourceDirectory);
            var server = ResolveServer(wapk, sourceDirectory);
            var port = ports.AllocatePort(repository.ListInstalled());
            var manifestName = $"{GetRepoName(wapk.Package.Id)}.webapp";
            var manifestPath = Path.Combine(installDirectory, manifestName);
            var manifest = new WebAppManifest(
                1,
                DateTimeOffset.UtcNow,
                wapk.Source.Commit,
                wapk.Package,
                new InstalledPaths("source", "data", "logs", "temp"),
                wapk.Runtime,
                new EntryInfo(wapk.Entry.Html, null, null, null, mode, server),
                new NetworkInfo("127.0.0.1", port, $"http://127.0.0.1:{port}"),
                new StorageInfo("persistent", false, false),
                wapk.Window);

            TomlManifestStore.SaveWebApp(manifestPath, manifest);
            var app = new InstalledApp(manifest, installDirectory, manifestPath);
            await InstallDependenciesAsync(app, cancellationToken);
            return app;
        }
        catch
        {
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }

            throw;
        }
    }

    public void Remove(string packageId, string? version = null)
    {
        var app = repository.GetInstalled(packageId, version);
        Directory.Delete(app.InstallDirectory, recursive: true);
    }

    public void ReassignPort(string packageId, int port, string? version = null)
    {
        if (port is < PortManager.FirstPort or > PortManager.LastPort)
        {
            throw new InvalidOperationException("Port must be in the 52000..52999 range.");
        }

        var app = repository.GetInstalled(packageId, version);
        if (ports.IsPortInUse(port))
        {
            throw new InvalidOperationException($"Port {port} is currently in use.");
        }

        var duplicate = repository.ListInstalled().FirstOrDefault(other =>
            !string.Equals(other.ManifestPath, app.ManifestPath, StringComparison.OrdinalIgnoreCase) &&
            other.Manifest.Network.Port == port);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Port {port} is already assigned to {duplicate.Manifest.Package.Id}/{duplicate.Manifest.Package.Version}.");
        }

        var updated = app.Manifest with
        {
            Network = new NetworkInfo("127.0.0.1", port, $"http://127.0.0.1:{port}")
        };
        TomlManifestStore.SaveWebApp(app.ManifestPath, updated);
    }

    private async Task<string> CheckoutAsync(WapkManifest wapk, CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(paths.GitCache, $"{wapk.Source.Owner}@{wapk.Source.Repo}");
        if (!Directory.Exists(cacheDirectory))
        {
            var url = $"https://github.com/{wapk.Source.Owner}/{wapk.Source.Repo}.git";
            await RunCheckedAsync(tools.Git, $"clone --no-checkout {Quote(url)} {Quote(cacheDirectory)}", paths.GitCache, cancellationToken);
        }

        await RunCheckedAsync(tools.Git, $"fetch origin {Quote(wapk.Source.Branch)}", cacheDirectory, cancellationToken);
        await RunCheckedAsync(tools.Git, $"checkout --force {Quote(wapk.Source.Commit)}", cacheDirectory, cancellationToken);
        var result = await CommandRunner.RunAsync(tools.Git, "rev-parse HEAD", cacheDirectory, cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }

        var actual = result.StandardOutput.Trim();
        if (!actual.StartsWith(wapk.Source.Commit, StringComparison.OrdinalIgnoreCase) &&
            !wapk.Source.Commit.StartsWith(actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Git commit mismatch. Expected {wapk.Source.Commit}, got {actual}.");
        }

        return cacheDirectory;
    }

    private async Task InstallDependenciesAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        if (!app.Manifest.Runtime.Python.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(app.SourceDirectory, "pyproject.toml")) &&
            File.Exists(Path.Combine(app.SourceDirectory, "uv.lock")))
        {
            var env = new Dictionary<string, string>
            {
                ["UV_CACHE_DIR"] = paths.UvCache,
                ["UV_PROJECT_ENVIRONMENT"] = app.VenvDirectory
            };
            await RunCheckedAsync(tools.Uv, "sync --frozen", app.SourceDirectory, cancellationToken, env);
        }

        if (!app.Manifest.Runtime.Node.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(app.SourceDirectory, "package.json")) &&
            File.Exists(Path.Combine(app.SourceDirectory, "pnpm-lock.yaml")))
        {
            var env = new Dictionary<string, string>
            {
                ["PNPM_HOME"] = Path.Combine(paths.Tools, "pnpm"),
                ["WEBAPP_NODE_MODULES"] = app.NodeModulesDirectory
            };
            await RunCheckedAsync(tools.Pnpm, $"install --frozen-lockfile --store-dir {Quote(paths.PnpmStore)}", app.SourceDirectory, cancellationToken, env);
            var sourceNodeModules = Path.Combine(app.SourceDirectory, "node_modules");
            if (Directory.Exists(sourceNodeModules) && !Directory.Exists(app.NodeModulesDirectory))
            {
                Directory.Move(sourceNodeModules, app.NodeModulesDirectory);
            }
        }
    }

    private static string ResolveMode(WapkManifest wapk, string sourceDirectory)
    {
        return ResolveServer(wapk, sourceDirectory) is null ? "static" : "server";
    }

    private static string? ResolveServer(WapkManifest wapk, string sourceDirectory)
    {
        if (!string.IsNullOrWhiteSpace(wapk.Entry.Python) && File.Exists(Path.Combine(sourceDirectory, wapk.Entry.Python)))
        {
            return wapk.Entry.Python;
        }

        if (!string.IsNullOrWhiteSpace(wapk.Entry.Node) && File.Exists(Path.Combine(sourceDirectory, wapk.Entry.Node)))
        {
            return wapk.Entry.Node;
        }

        return null;
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path escapes the checkout directory.");
        }

        return fullPath;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in EnumerateDirectories(source))
        {
            var targetDirectory = Path.Combine(destination, Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(targetDirectory);
        }

        foreach (var file in EnumerateFiles(source))
        {
            var targetFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return directory;
            foreach (var child in EnumerateDirectories(directory))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root))
        {
            yield return file;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in EnumerateFiles(directory))
            {
                yield return file;
            }
        }
    }

    private static string GetRepoName(string packageId)
    {
        var at = packageId.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? packageId[(at + 1)..] : packageId;
    }

    private static async Task RunCheckedAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IDictionary<string, string>? environment = null)
    {
        var result = await CommandRunner.RunAsync(fileName, arguments, workingDirectory, environment, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.Trim().Length > 0 ? result.StandardError.Trim() : result.StandardOutput.Trim());
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
