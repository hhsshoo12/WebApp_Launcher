using System.Text.Json;

namespace WebAppLauncher.Core;

public sealed class AppUpdateManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly WebAppPaths paths;
    private readonly GitSourceResolver git;
    private readonly AppInstaller installer;

    public AppUpdateManager(WebAppPaths paths)
    {
        this.paths = paths;
        git = new GitSourceResolver(paths);
        installer = new AppInstaller(paths);
    }

    public async Task<AppUpdateStatus> CheckAsync(
        InstalledApp app,
        CancellationToken cancellationToken = default)
    {
        if (app.Manifest.Source is null)
        {
            return Status(app, null, "unsupported", "앱 소스 정보가 없습니다.");
        }

        if (app.Manifest.Source.Commit != "*")
        {
            return Status(app, app.Manifest.SourceCommit, "pinned", "고정 커밋 앱입니다.");
        }

        try
        {
            var resolved = await git.ResolveAsync(
                app.Manifest.Source,
                createSnapshot: false,
                cancellationToken);
            var status = resolved.Commit.Equals(app.Manifest.SourceCommit, StringComparison.OrdinalIgnoreCase)
                ? "current"
                : HasPrepared(app.Manifest.Package.Id, resolved.Commit) ? "pending" : "available";
            return Status(app, resolved.Commit, status);
        }
        catch (Exception ex)
        {
            return Status(app, null, "error", ex.Message);
        }
    }

    public async Task<PreparedAppUpdate?> PrepareAsync(
        InstalledApp app,
        CancellationToken cancellationToken = default)
    {
        if (app.Manifest.Source is null)
        {
            throw new InvalidOperationException("App source metadata is required for updates.");
        }

        if (app.Manifest.Source.Commit != "*")
        {
            throw new InvalidOperationException("Pinned commit apps do not receive updates.");
        }

        var resolved = await git.ResolveAsync(
            app.Manifest.Source,
            cancellationToken: cancellationToken);
        if (resolved.Commit.Equals(app.Manifest.SourceCommit, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var newVersion = resolved.Commit[..8];
        var stagingDirectory = GetStagingDirectory(app.Manifest.Package.Id, resolved.Commit);
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        var wapk = ToWapk(app.Manifest);
        var recipePath = Path.Combine(app.SourceDirectory, "app.wapk");
        var preparedApp = await installer.InstallResolvedAsync(
            wapk,
            resolved,
            stagingDirectory,
            File.Exists(recipePath) ? recipePath : null,
            cancellationToken);
        var prepared = new PreparedAppUpdate(
            app.Manifest.Package.Id,
            app.Manifest.Package.Version,
            preparedApp.Manifest.Package.Version,
            resolved.Commit,
            stagingDirectory);
        File.WriteAllText(
            Path.Combine(stagingDirectory, "pending-update.json"),
            JsonSerializer.Serialize(prepared, JsonOptions));
        return prepared;
    }

    public InstalledApp Apply(InstalledApp oldApp, PreparedAppUpdate prepared)
    {
        if (!oldApp.Manifest.Package.Id.Equals(prepared.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Prepared update does not match the installed app.");
        }

        var stagedManifestPath = Directory
            .EnumerateFiles(prepared.StagingDirectory, "*.webapp", SearchOption.TopDirectoryOnly)
            .Single();
        var stagedManifest = TomlManifestStore.LoadWebApp(stagedManifestPath);
        if (!stagedManifest.SourceCommit.Equals(prepared.Commit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Prepared update commit mismatch.");
        }

        var targetDirectory = paths.GetAppDirectory(prepared.PackageId, prepared.NewVersion);
        if (Directory.Exists(targetDirectory))
        {
            var targetManifestPath = Directory
                .EnumerateFiles(targetDirectory, "*.webapp", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (targetManifestPath is null ||
                !TomlManifestStore.LoadWebApp(targetManifestPath).SourceCommit.Equals(
                    prepared.Commit,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Commit prefix collision for version {prepared.NewVersion}.");
            }

            CopyPersistentDirectory(oldApp.DataDirectory, Path.Combine(targetDirectory, "data"));
            CopyPersistentDirectory(oldApp.LogDirectory, Path.Combine(targetDirectory, "logs"));
            Directory.Delete(prepared.StagingDirectory, recursive: true);
            var existing = new InstalledApp(
                TomlManifestStore.LoadWebApp(targetManifestPath),
                targetDirectory,
                targetManifestPath);
            if (!oldApp.InstallDirectory.Equals(targetDirectory, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(oldApp.InstallDirectory))
            {
                Directory.Delete(oldApp.InstallDirectory, recursive: true);
            }

            return existing;
        }

        CopyPersistentDirectory(oldApp.DataDirectory, Path.Combine(prepared.StagingDirectory, "data"));
        CopyPersistentDirectory(oldApp.LogDirectory, Path.Combine(prepared.StagingDirectory, "logs"));
        var marker = Path.Combine(prepared.StagingDirectory, "pending-update.json");
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
        Directory.Move(prepared.StagingDirectory, targetDirectory);
        var targetManifest = Path.Combine(targetDirectory, Path.GetFileName(stagedManifestPath));
        var installed = new InstalledApp(stagedManifest, targetDirectory, targetManifest);

        if (!oldApp.InstallDirectory.Equals(targetDirectory, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(oldApp.InstallDirectory))
        {
            Directory.Delete(oldApp.InstallDirectory, recursive: true);
        }

        return installed;
    }

    public IReadOnlyList<PreparedAppUpdate> ListPrepared()
    {
        if (!Directory.Exists(paths.AppUpdates))
        {
            return [];
        }

        var updates = new List<PreparedAppUpdate>();
        foreach (var marker in Directory.EnumerateFiles(
                     paths.AppUpdates,
                     "pending-update.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                var value = JsonSerializer.Deserialize<PreparedAppUpdate>(
                    File.ReadAllText(marker),
                    JsonOptions);
                if (value is not null)
                {
                    updates.Add(value);
                }
            }
            catch (JsonException)
            {
            }
        }

        return updates;
    }

    private bool HasPrepared(string packageId, string commit)
    {
        return File.Exists(Path.Combine(
            GetStagingDirectory(packageId, commit),
            "pending-update.json"));
    }

    private string GetStagingDirectory(string packageId, string commit)
    {
        return Path.Combine(
            paths.AppUpdates,
            WebAppPaths.SanitizeSegment(packageId),
            commit);
    }

    private static AppUpdateStatus Status(
        InstalledApp app,
        string? latestCommit,
        string status,
        string? message = null)
    {
        return new AppUpdateStatus(
            app.Manifest.Package.Id,
            app.Manifest.Package.Name,
            app.Manifest.Package.Version,
            app.Manifest.SourceCommit,
            latestCommit?[..8],
            latestCommit,
            status,
            message);
    }

    private static WapkManifest ToWapk(WebAppManifest manifest)
    {
        return new WapkManifest(
            2,
            manifest.Package with { Version = string.Empty },
            manifest.Source!,
            manifest.Runtime,
            new EntryInfo(
                manifest.Entry.Html,
                manifest.Entry.Server?.EndsWith(".py", StringComparison.OrdinalIgnoreCase) == true
                    ? manifest.Entry.Server
                    : null,
                manifest.Entry.Server?.EndsWith(".js", StringComparison.OrdinalIgnoreCase) == true
                    ? manifest.Entry.Server
                    : null,
                manifest.Entry.Icon),
            manifest.Window);
    }

    private static void CopyPersistentDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        AppInstaller.CopyDirectory(source, destination);
    }
}
