namespace WebAppLauncher.Core;

public sealed class AppRepository
{
    private readonly WebAppPaths paths;

    public AppRepository(WebAppPaths paths)
    {
        this.paths = paths;
    }

    public IReadOnlyList<InstalledApp> ListInstalled()
    {
        if (!Directory.Exists(paths.Apps))
        {
            return [];
        }

        var apps = new List<InstalledApp>();
        foreach (var manifestPath in Directory.EnumerateFiles(paths.Apps, "*.webapp", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = TomlManifestStore.LoadWebApp(manifestPath);
                apps.Add(new InstalledApp(manifest, Path.GetDirectoryName(manifestPath)!, manifestPath));
            }
            catch
            {
                // Broken app metadata should not hide the rest of the launcher.
            }
        }

        return apps
            .OrderBy(app => app.Manifest.Package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.Manifest.Package.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public InstalledApp GetInstalled(string packageId, string? version = null)
    {
        var candidates = ListInstalled()
            .Where(app => string.Equals(app.Manifest.Package.Id, packageId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (version is not null)
        {
            candidates = candidates
                .Where(app => string.Equals(app.Manifest.Package.Version, version, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return candidates.Count switch
        {
            0 => throw new InvalidOperationException($"Installed app not found: {packageId}"),
            1 => candidates[0],
            _ => throw new InvalidOperationException($"Multiple versions installed for {packageId}. Specify --version.")
        };
    }
}
