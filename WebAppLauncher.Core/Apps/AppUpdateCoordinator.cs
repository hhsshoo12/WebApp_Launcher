namespace WebAppLauncher.Core;

public sealed record AppUpdateView(
    string Status,
    string? LatestVersion,
    string? LatestCommit,
    string? Message);

public sealed record PreparedUpdateResult(
    PreparedAppUpdate Prepared,
    bool Applied,
    InstalledApp? Installed);

public sealed class AppUpdateCoordinator
{
    private readonly AppRepository repository;
    private readonly AppUpdateManager manager;
    private readonly Dictionary<string, AppUpdateStatus> statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PreparedAppUpdate> prepared =
        new(StringComparer.OrdinalIgnoreCase);

    public AppUpdateCoordinator(AppRepository repository, AppUpdateManager manager)
    {
        this.repository = repository;
        this.manager = manager;
        foreach (var update in manager.ListPrepared())
        {
            prepared[update.PackageId] = update;
        }
    }

    public IReadOnlyCollection<AppUpdateStatus> Statuses => statuses.Values;

    public void RemovePackage(string packageId)
    {
        prepared.Remove(packageId);
        statuses.Remove(packageId);
    }

    public AppUpdateView? StateFor(InstalledApp app)
    {
        if (prepared.TryGetValue(app.Manifest.Package.Id, out var update))
        {
            return new AppUpdateView("pending", update.NewVersion, update.Commit, null);
        }

        return statuses.TryGetValue(app.Manifest.Package.Id, out var status)
            ? new AppUpdateView(status.Status, status.LatestVersion, status.LatestCommit, status.Message)
            : null;
    }

    public async Task CheckInstalledAsync(
        bool prepareAvailable,
        Func<string, bool> isPackageIdle,
        CancellationToken cancellationToken = default)
    {
        foreach (var app in repository.ListInstalled())
        {
            var status = await manager.CheckAsync(app, cancellationToken);
            statuses[app.Manifest.Package.Id] = status;
            if (!prepareAvailable || status.Status != "available")
            {
                continue;
            }

            try
            {
                var result = await PrepareAsync(app, isPackageIdle, cancellationToken);
                if (result is not null)
                {
                    statuses[result.Prepared.PackageId] = status with { Status = "pending" };
                }
            }
            catch (Exception ex)
            {
                statuses[app.Manifest.Package.Id] =
                    status with { Status = "error", Message = ex.Message };
            }
        }
    }

    public async Task<PreparedUpdateResult?> PrepareAsync(
        InstalledApp app,
        Func<string, bool> isPackageIdle,
        CancellationToken cancellationToken = default)
    {
        var update = await manager.PrepareAsync(app, cancellationToken);
        if (update is null)
        {
            statuses[app.Manifest.Package.Id] = await manager.CheckAsync(app, cancellationToken);
            return null;
        }

        prepared[update.PackageId] = update;
        statuses[update.PackageId] = new AppUpdateStatus(
            update.PackageId,
            app.Manifest.Package.Name,
            update.OldVersion,
            app.Manifest.SourceCommit,
            update.NewVersion,
            update.Commit,
            "pending");
        var applied = ApplyIfIdle(update.PackageId, isPackageIdle, out var installed);
        return new PreparedUpdateResult(update, applied, installed);
    }

    public async Task PrepareAllAsync(
        Func<string, bool> isPackageIdle,
        CancellationToken cancellationToken = default)
    {
        foreach (var app in repository.ListInstalled())
        {
            if (app.Manifest.Source?.Commit != "*")
            {
                continue;
            }

            try
            {
                await PrepareAsync(app, isPackageIdle, cancellationToken);
            }
            catch (Exception ex)
            {
                statuses[app.Manifest.Package.Id] = new AppUpdateStatus(
                    app.Manifest.Package.Id,
                    app.Manifest.Package.Name,
                    app.Manifest.Package.Version,
                    app.Manifest.SourceCommit,
                    null,
                    null,
                    "error",
                    ex.Message);
            }
        }
    }

    public bool ApplyIfIdle(string packageId, Func<string, bool> isPackageIdle)
    {
        return ApplyIfIdle(packageId, isPackageIdle, out _);
    }

    private bool ApplyIfIdle(
        string packageId,
        Func<string, bool> isPackageIdle,
        out InstalledApp? installed)
    {
        installed = null;
        if (!prepared.TryGetValue(packageId, out var update) || !isPackageIdle(packageId))
        {
            return false;
        }

        var oldApp = repository.GetInstalled(packageId, update.OldVersion);
        installed = manager.Apply(oldApp, update);
        prepared.Remove(packageId);
        statuses[packageId] = new AppUpdateStatus(
            packageId,
            installed.Manifest.Package.Name,
            installed.Manifest.Package.Version,
            installed.Manifest.SourceCommit,
            installed.Manifest.Package.Version,
            installed.Manifest.SourceCommit,
            "current");
        return true;
    }
}
