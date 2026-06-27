using WebAppLauncher.Core;
using Xunit;
using System.Runtime.Versioning;

namespace WebAppLauncher.Tests;

[SupportedOSPlatform("windows")]
public sealed class AppShortcutServiceTests
{
    [Fact]
    public void CreateDesktopShortcutWritesExpectedTarget()
    {
        using var temp = new TempDirectory();
        using var installTemp = new TempDirectory();
        var desktop = Path.Combine(temp.Path, "Desktop");
        Directory.CreateDirectory(desktop);

        var launcherExe = Path.Combine(temp.Path, "WebAppLauncher.exe");
        File.WriteAllBytes(launcherExe, new byte[] { 0x4D, 0x5A });

        var manifestPath = Path.Combine(installTemp.Path, "owner@repo.webapp");
        var sourceDir = Path.Combine(installTemp.Path, "source");
        Directory.CreateDirectory(sourceDir);
        var manifest = new WebAppManifest(
            Format: 2,
            InstalledAt: DateTimeOffset.UnixEpoch,
            SourceCommit: "abcdef123456",
            Package: new PackageInfo("owner@repo", "Sample App", "0.1.0"),
            Paths: new InstalledPaths("source", "data", "logs", "temp"),
            Runtime: new RuntimeInfo("python313", "none"),
            Entry: new EntryInfo("index.html", null, null, null, Mode: "static", Server: null),
            Network: new NetworkInfo("127.0.0.1", 0, "dynamic"),
            Storage: new StorageInfo("ephemeral", false, false),
            Window: new WindowInfo(800, 600, true, false),
            Source: new SourceInfo("github", "owner", "repo", "main", "*", "."));
        TomlManifestStore.SaveWebApp(manifestPath, manifest);

        var app = new InstalledApp(manifest, installTemp.Path, manifestPath);
        var writer = new RecordingShortcutWriter();
        var service = new AppShortcutService(() => desktop, writer);

        var shortcutPath = service.CreateDesktopShortcut(app, launcherExe);

        Assert.Equal(Path.Combine(desktop, "Sample App.lnk"), shortcutPath);
        Assert.Equal(launcherExe, writer.LastTarget);
        Assert.Equal($"\"{manifestPath}\"", writer.LastArguments);
        Assert.Equal(temp.Path, writer.LastWorkingDirectory);
    }

    [Fact]
    public void CreateDesktopShortcutPicksIcoEntryIconWhenPresent()
    {
        using var temp = new TempDirectory();
        using var installTemp = new TempDirectory();
        var desktop = Path.Combine(temp.Path, "Desktop");
        Directory.CreateDirectory(desktop);
        var launcherExe = Path.Combine(temp.Path, "WebAppLauncher.exe");
        File.WriteAllBytes(launcherExe, new byte[] { 0x4D, 0x5A });

        var manifestPath = Path.Combine(installTemp.Path, "owner@repo.webapp");
        var sourceDir = Path.Combine(installTemp.Path, "source");
        var iconDir = Path.Combine(sourceDir, "assets");
        Directory.CreateDirectory(iconDir);
        var iconPath = Path.Combine(iconDir, "app.ico");
        File.WriteAllBytes(iconPath, new byte[] { 0x00, 0x00, 0x01, 0x00 });

        var manifest = new WebAppManifest(
            Format: 2,
            InstalledAt: DateTimeOffset.UnixEpoch,
            SourceCommit: "abcdef123456",
            Package: new PackageInfo("owner@repo", "Ico App", "0.1.0"),
            Paths: new InstalledPaths("source", "data", "logs", "temp"),
            Runtime: new RuntimeInfo("python313", "none"),
            Entry: new EntryInfo("index.html", null, null, "assets/app.ico", Mode: "static", Server: null),
            Network: new NetworkInfo("127.0.0.1", 0, "dynamic"),
            Storage: new StorageInfo("ephemeral", false, false),
            Window: new WindowInfo(800, 600, true, false),
            Source: new SourceInfo("github", "owner", "repo", "main", "*", "."));
        TomlManifestStore.SaveWebApp(manifestPath, manifest);
        var app = new InstalledApp(manifest, installTemp.Path, manifestPath);

        var writer = new RecordingShortcutWriter();
        var service = new AppShortcutService(() => desktop, writer);
        service.CreateDesktopShortcut(app, launcherExe);

        Assert.Equal($"{iconPath},0", writer.LastIconLocation);
    }

    [Fact]
    public void CreateDesktopShortcutFallsBackWhenIconIsNotIco()
    {
        using var temp = new TempDirectory();
        using var installTemp = new TempDirectory();
        var desktop = Path.Combine(temp.Path, "Desktop");
        Directory.CreateDirectory(desktop);
        var launcherExe = Path.Combine(temp.Path, "WebAppLauncher.exe");
        File.WriteAllBytes(launcherExe, new byte[] { 0x4D, 0x5A });

        var manifestPath = Path.Combine(installTemp.Path, "owner@repo.webapp");
        var sourceDir = Path.Combine(installTemp.Path, "source");
        var assetDir = Path.Combine(sourceDir, "assets");
        Directory.CreateDirectory(assetDir);
        File.WriteAllBytes(Path.Combine(assetDir, "logo.png"), new byte[] { 1, 2, 3 });

        var manifest = new WebAppManifest(
            Format: 2,
            InstalledAt: DateTimeOffset.UnixEpoch,
            SourceCommit: "abcdef123456",
            Package: new PackageInfo("owner@repo", "Png App", "0.1.0"),
            Paths: new InstalledPaths("source", "data", "logs", "temp"),
            Runtime: new RuntimeInfo("python313", "none"),
            Entry: new EntryInfo("index.html", null, null, "assets/logo.png", Mode: "static", Server: null),
            Network: new NetworkInfo("127.0.0.1", 0, "dynamic"),
            Storage: new StorageInfo("ephemeral", false, false),
            Window: new WindowInfo(800, 600, true, false),
            Source: new SourceInfo("github", "owner", "repo", "main", "*", "."));
        TomlManifestStore.SaveWebApp(manifestPath, manifest);
        var app = new InstalledApp(manifest, installTemp.Path, manifestPath);

        var writer = new RecordingShortcutWriter();
        var service = new AppShortcutService(() => desktop, writer);
        service.CreateDesktopShortcut(app, launcherExe);

        Assert.Equal(string.Empty, writer.LastIconLocation);
    }

    [Fact]
    public void CreateDesktopShortcutSanitizesInvalidFileNameCharacters()
    {
        using var temp = new TempDirectory();
        using var installTemp = new TempDirectory();
        var desktop = Path.Combine(temp.Path, "Desktop");
        Directory.CreateDirectory(desktop);
        var launcherExe = Path.Combine(temp.Path, "WebAppLauncher.exe");
        File.WriteAllBytes(launcherExe, new byte[] { 0x4D, 0x5A });

        var manifestPath = Path.Combine(installTemp.Path, "owner@repo.webapp");
        var sourceDir = Path.Combine(installTemp.Path, "source");
        Directory.CreateDirectory(sourceDir);

        var manifest = new WebAppManifest(
            Format: 2,
            InstalledAt: DateTimeOffset.UnixEpoch,
            SourceCommit: "abcdef123456",
            Package: new PackageInfo("owner@repo", "weird:name*?", "0.1.0"),
            Paths: new InstalledPaths("source", "data", "logs", "temp"),
            Runtime: new RuntimeInfo("python313", "none"),
            Entry: new EntryInfo("index.html", null, null, null, Mode: "static", Server: null),
            Network: new NetworkInfo("127.0.0.1", 0, "dynamic"),
            Storage: new StorageInfo("ephemeral", false, false),
            Window: new WindowInfo(800, 600, true, false),
            Source: new SourceInfo("github", "owner", "repo", "main", "*", "."));
        TomlManifestStore.SaveWebApp(manifestPath, manifest);
        var app = new InstalledApp(manifest, installTemp.Path, manifestPath);

        var writer = new RecordingShortcutWriter();
        var service = new AppShortcutService(() => desktop, writer);
        var shortcutPath = service.CreateDesktopShortcut(app, launcherExe);

        Assert.Equal(Path.Combine(desktop, "weird_name__.lnk"), shortcutPath);
    }

    [Fact]
    public void FindByManifestPathReturnsNullWhenUnknown()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);
        paths.EnsureRootLayout();
        var repository = new AppRepository(paths);

        Assert.Null(repository.FindByManifestPath(Path.Combine(temp.Path, "missing.webapp")));
    }

    [Fact]
    public void ShellLinkShortcutWriterProducesValidWindowsLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var launcherExe = Path.Combine(temp.Path, "WebAppLauncher.exe");
        File.WriteAllBytes(launcherExe, new byte[] { 0x4D, 0x5A });
        var shortcutPath = Path.Combine(temp.Path, "Sample.lnk");

        new ShellLinkShortcutWriter().Write(
            shortcutPath: shortcutPath,
            targetPath: launcherExe,
            arguments: "\"C:\\sample\\app.webapp\"",
            workingDirectory: temp.Path,
            iconLocation: string.Empty,
            description: "Sample app");

        Assert.True(File.Exists(shortcutPath));
        var header = new byte[4];
        using (var stream = File.OpenRead(shortcutPath))
        {
            Assert.Equal(4, stream.Read(header, 0, 4));
        }
        Assert.Equal(0x4C, header[0]);
    }

    private sealed class RecordingShortcutWriter : IShortcutWriter
    {
        public string LastTarget { get; private set; } = string.Empty;
        public string LastArguments { get; private set; } = string.Empty;
        public string LastWorkingDirectory { get; private set; } = string.Empty;
        public string LastIconLocation { get; private set; } = string.Empty;

        public void Write(
            string shortcutPath,
            string targetPath,
            string arguments,
            string workingDirectory,
            string iconLocation,
            string description)
        {
            LastTarget = targetPath;
            LastArguments = arguments;
            LastWorkingDirectory = workingDirectory;
            LastIconLocation = iconLocation;
            File.WriteAllBytes(shortcutPath, new byte[] { 0x4C, 0x00, 0x00, 0x00 });
        }
    }
}
