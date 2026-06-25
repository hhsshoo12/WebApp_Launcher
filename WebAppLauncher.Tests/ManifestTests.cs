using WebAppLauncher.Core;

namespace WebAppLauncher.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void LoadWapkAcceptsValidManifest()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("app.wapk", ValidWapk());

        var manifest = TomlManifestStore.LoadWapk(path);

        Assert.Equal("hhsshoo12@webapp-test", manifest.Package.Id);
        Assert.Equal("github", manifest.Source.Provider);
        Assert.Equal("python313", manifest.Runtime.Python);
        Assert.Equal("app.html", manifest.Entry.Html);
    }

    [Fact]
    public void LoadWapkRejectsPathTraversal()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("bad.wapk", ValidWapk().Replace("app_dir = \".\"", "app_dir = \"..\""));

        Assert.Throws<InvalidDataException>(() => TomlManifestStore.LoadWapk(path));
    }

    [Fact]
    public void LoadWapkRejectsRemovedPython312Runtime()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("old-runtime.wapk", ValidWapk().Replace("python313", "python312"));

        Assert.Throws<InvalidDataException>(() => TomlManifestStore.LoadWapk(path));
    }

    [Fact]
    public void SaveAndLoadWebAppRoundTripsManifest()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "webapp-test.webapp");
        var manifest = new WebAppManifest(
            1,
            DateTimeOffset.Parse("2026-06-23T00:00:00Z"),
            "abcdef1234567890",
            new PackageInfo("hhsshoo12@webapp-test", "WebApp Test", "1.0"),
            new InstalledPaths("source", "data", "logs", "temp"),
            new RuntimeInfo("python313", "nodejs-lts-22"),
            new EntryInfo("app.html", null, null, null, "server", "app.py"),
            new NetworkInfo("127.0.0.1", 0, "dynamic"),
            new StorageInfo("ephemeral", false, false),
            new WindowInfo(1200, 800, true, false));

        TomlManifestStore.SaveWebApp(path, manifest);
        var loaded = TomlManifestStore.LoadWebApp(path);

        Assert.Equal(manifest.Package, loaded.Package);
        Assert.Equal(manifest.Network, loaded.Network);
        Assert.Equal(manifest.Entry.Server, loaded.Entry.Server);
    }

    [Fact]
    public void RepositoryMigratesLegacyRuntimePortAndBrowserProfile()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);
        paths.EnsureRootLayout();
        var installDirectory = paths.GetAppDirectory("hhsshoo12@webapp-test", "1.0");
        Directory.CreateDirectory(installDirectory);
        var manifestPath = Path.Combine(installDirectory, "webapp-test.webapp");
        var legacyManifest = new WebAppManifest(
            1,
            DateTimeOffset.Parse("2026-06-23T00:00:00Z"),
            "abcdef1234567890",
            new PackageInfo("hhsshoo12@webapp-test", "WebApp Test", "1.0"),
            new InstalledPaths("source", "data", "logs", "temp"),
            new RuntimeInfo("python312", "none"),
            new EntryInfo("app.html", null, null, null, "server", "app.py"),
            new NetworkInfo("127.0.0.1", 52017, "http://127.0.0.1:52017"),
            new StorageInfo("persistent", false, false),
            new WindowInfo(1200, 800, true, false));
        TomlManifestStore.SaveWebApp(manifestPath, legacyManifest);

        var installed = new AppRepository(paths).ListInstalled();
        var migrated = TomlManifestStore.LoadWebApp(manifestPath);

        Assert.Single(installed);
        Assert.Equal("python313", installed[0].Manifest.Runtime.Python);
        Assert.Equal("python313", migrated.Runtime.Python);
        Assert.Equal(0, migrated.Network.Port);
        Assert.Equal("dynamic", migrated.Network.Origin);
        Assert.Equal("ephemeral", migrated.Storage.BrowserProfile);
        Assert.DoesNotContain("python312", File.ReadAllText(manifestPath));
    }

    [Fact]
    public void PortManagerAllocatesNearestFreePortAndReusesReleasedPort()
    {
        var manager = new PortManager(port => port == PortManager.FirstPort);

        var first = manager.AllocatePort();
        var second = manager.AllocatePort();
        manager.ReleasePort(first);
        var reused = manager.AllocatePort();

        Assert.Equal(PortManager.FirstPort + 1, first);
        Assert.Equal(PortManager.FirstPort + 2, second);
        Assert.Equal(first, reused);
    }

    [Fact]
    public void LaunchResultReleasesPortOnlyOnce()
    {
        var releases = 0;
        var result = new LaunchResult(
            null!,
            new Uri("http://127.0.0.1:52000"),
            null,
            null,
            52000,
            () => releases++);

        result.ReleasePort();
        result.ReleasePort();

        Assert.Equal(1, releases);
    }

    [Fact]
    public void LauncherSettingsStorePersistsDeveloperMode()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);
        var store = new LauncherSettingsStore(paths);

        store.Save(new LauncherSettings(DeveloperMode: true));
        var loaded = store.Load();

        Assert.True(loaded.DeveloperMode);
        Assert.True(File.Exists(Path.Combine(temp.Path, "launcher-settings.json")));
    }

    [Fact]
    public void WebAppPathsCreatesExpectedRootLayout()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);

        paths.EnsureRootLayout();

        Assert.True(Directory.Exists(Path.Combine(temp.Path, "runtime", "python313")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "runtime", "nodejs-lts-24")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "tools", "git")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "packages", "pnpm-store")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "app")));
    }

    private static string ValidWapk()
    {
        return """
        [wapk]
        format = 1

        [package]
        id = "hhsshoo12@webapp-test"
        name = "WebApp Test"
        version = "1.0"

        [source]
        provider = "github"
        owner = "hhsshoo12"
        repo = "webapp-test"
        branch = "main"
        commit = "abcdef1234567890"
        app_dir = "."

        [runtime]
        python = "python313"
        node = "nodejs-lts-22"

        [entry]
        html = "app.html"
        python = "app.py"
        node = "app.js"
        icon = "icon.png"

        [window]
        width = 1200
        height = 800
        resizable = true
        devtools = false
        """;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "webapp-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string fileName, string content)
    {
        var path = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
