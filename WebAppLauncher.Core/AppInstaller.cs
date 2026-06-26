namespace WebAppLauncher.Core;

public sealed class AppInstaller
{
    private readonly WebAppPaths paths;
    private readonly ToolResolver tools;
    private readonly AppRepository repository;
    private readonly GitSourceResolver git;

    public AppInstaller(WebAppPaths paths)
    {
        this.paths = paths;
        tools = new ToolResolver(paths);
        repository = new AppRepository(paths);
        git = new GitSourceResolver(paths);
    }

    public async Task<InstalledApp> InstallAsync(string wapkPath, CancellationToken cancellationToken = default)
    {
        paths.EnsureRootLayout();
        var wapk = TomlManifestStore.LoadWapk(wapkPath);
        var resolved = await git.ResolveAsync(wapk.Source, cancellationToken: cancellationToken);
        var version = wapk.Format == 2 ? resolved.Commit[..8] : wapk.Package.Version;
        var installDirectory = paths.GetAppDirectory(wapk.Package.Id, version);
        return await InstallResolvedAsync(
            wapk,
            resolved,
            installDirectory,
            wapkPath,
            cancellationToken);
    }

    public async Task<InstalledApp> InstallResolvedAsync(
        WapkManifest wapk,
        ResolvedGitSource resolved,
        string installDirectory,
        string? recipePath = null,
        CancellationToken cancellationToken = default)
    {
        var sourceDirectory = Path.Combine(installDirectory, "source");

        if (Directory.Exists(installDirectory))
        {
            throw new InvalidOperationException(
                $"App version is already installed: {wapk.Package.Id}/{Path.GetFileName(installDirectory)}");
        }

        try
        {
            Directory.CreateDirectory(installDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(Path.Combine(installDirectory, "data"));
            Directory.CreateDirectory(Path.Combine(installDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(installDirectory, "temp"));

            var appSource = SafeCombine(resolved.CheckoutDirectory, wapk.Source.AppDir);
            CopyDirectory(appSource, sourceDirectory);
            if (!string.IsNullOrWhiteSpace(recipePath))
            {
                File.Copy(recipePath, Path.Combine(sourceDirectory, "app.wapk"), overwrite: true);
            }

            var htmlPath = Path.Combine(sourceDirectory, wapk.Entry.Html);
            if (!File.Exists(htmlPath))
            {
                throw new InvalidDataException($"Missing required app.html entry: {htmlPath}");
            }

            var mode = ResolveMode(wapk, sourceDirectory);
            var server = ResolveServer(wapk, sourceDirectory);
            var package = wapk.Package with
            {
                Version = wapk.Format == 2 ? resolved.Commit[..8] : wapk.Package.Version
            };
            var manifestName = $"{GetRepoName(wapk.Package.Id)}.webapp";
            var manifestPath = Path.Combine(installDirectory, manifestName);
            var manifest = new WebAppManifest(
                wapk.Format == 2 ? 2 : 1,
                DateTimeOffset.UtcNow,
                resolved.Commit,
                package,
                new InstalledPaths("source", "data", "logs", "temp"),
                wapk.Runtime,
                new EntryInfo(wapk.Entry.Html, null, null, wapk.Entry.Icon, mode, server),
                new NetworkInfo("127.0.0.1", 0, "dynamic"),
                new StorageInfo("ephemeral", false, false),
                wapk.Window,
                wapk.Format == 2 ? wapk.Source : null);

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
        var prepared = Path.Combine(paths.AppUpdates, WebAppPaths.SanitizeSegment(packageId));
        if (Directory.Exists(prepared))
        {
            Directory.Delete(prepared, recursive: true);
        }
    }

    public async Task InstallDependenciesAsync(InstalledApp app, CancellationToken cancellationToken = default)
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
            await RunCheckedAsync(tools.Uv, ["sync", "--frozen"], app.SourceDirectory, cancellationToken, env);
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
            await RunCheckedAsync(
                tools.Pnpm,
                ["install", "--frozen-lockfile", "--store-dir", paths.PnpmStore],
                app.SourceDirectory,
                cancellationToken,
                env);
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

    internal static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path escapes the checkout directory.");
        }

        return fullPath;
    }

    internal static void CopyDirectory(string source, string destination)
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
            if (Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                IsReparsePoint(directory))
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
            if (IsReparsePoint(file))
            {
                continue;
            }

            yield return file;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                IsReparsePoint(directory))
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
        IEnumerable<string> arguments,
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

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }
}
