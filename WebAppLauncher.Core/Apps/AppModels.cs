namespace WebAppLauncher.Core;

public sealed record ResolvedGitSource(
    SourceInfo Requested,
    string Branch,
    string Commit,
    string CheckoutDirectory);

public sealed record AppUpdateStatus(
    string PackageId,
    string Name,
    string InstalledVersion,
    string InstalledCommit,
    string? LatestVersion,
    string? LatestCommit,
    string Status,
    string? Message = null);

public sealed record InstalledApp(
    WebAppManifest Manifest,
    string InstallDirectory,
    string ManifestPath)
{
    public string SourceDirectory => Path.Combine(InstallDirectory, Manifest.Paths.Source);
    public string DataDirectory => Path.Combine(InstallDirectory, Manifest.Paths.Data);
    public string LogDirectory => Path.Combine(InstallDirectory, Manifest.Paths.Logs);
    public string TempDirectory => Path.Combine(InstallDirectory, Manifest.Paths.Temp);
    public string VenvDirectory => Path.Combine(InstallDirectory, ".venv");
    public string NodeModulesDirectory => Path.Combine(InstallDirectory, "node_modules");
}

public sealed record PreparedAppUpdate(
    string PackageId,
    string OldVersion,
    string NewVersion,
    string Commit,
    string StagingDirectory);
