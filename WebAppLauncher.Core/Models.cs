namespace WebAppLauncher.Core;

public sealed record PackageInfo(string Id, string Name, string Version);

public sealed record SourceInfo(
    string Provider,
    string Owner,
    string Repo,
    string Branch,
    string Commit,
    string AppDir);

public sealed record RuntimeInfo(string Python, string Node);

public sealed record EntryInfo(
    string Html,
    string? Python,
    string? Node,
    string? Icon,
    string? Mode = null,
    string? Server = null);

public sealed record WindowInfo(int Width, int Height, bool Resizable, bool Devtools);

public sealed record WapkManifest(
    int Format,
    PackageInfo Package,
    SourceInfo Source,
    RuntimeInfo Runtime,
    EntryInfo Entry,
    WindowInfo Window);

public sealed record InstalledPaths(string Source, string Data, string Logs, string Temp);

public sealed record NetworkInfo(string Host, int Port, string Origin);

public sealed record StorageInfo(string BrowserProfile, bool Pwa, bool ServiceWorker);

public sealed record WebAppManifest(
    int Format,
    DateTimeOffset InstalledAt,
    string SourceCommit,
    PackageInfo Package,
    InstalledPaths Paths,
    RuntimeInfo Runtime,
    EntryInfo Entry,
    NetworkInfo Network,
    StorageInfo Storage,
    WindowInfo Window);

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

public sealed record LaunchResult(
    InstalledApp App,
    Uri Uri,
    System.Diagnostics.Process? Process,
    string? LogPath,
    int? Port,
    Action? ReleasePortAction = null)
{
    private int portReleased;

    public bool HasBackend => Process is not null;

    public void ReleasePort()
    {
        if (Interlocked.Exchange(ref portReleased, 1) == 0)
        {
            ReleasePortAction?.Invoke();
        }
    }
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
