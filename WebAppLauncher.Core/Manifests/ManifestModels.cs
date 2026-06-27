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

public sealed record WindowInfo(
    int Width,
    int Height,
    bool Resizable,
    bool Devtools,
    bool Transparent = false,
    bool Borderless = false,
    bool Fullscreen = false,
    bool AlwaysOnTop = false,
    bool StartMaximized = false,
    string InstanceMode = "new_backend");

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
    WindowInfo Window,
    SourceInfo? Source = null);
