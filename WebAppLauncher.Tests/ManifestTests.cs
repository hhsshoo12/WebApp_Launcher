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
            new NetworkInfo("127.0.0.1", 52017, "http://127.0.0.1:52017"),
            new StorageInfo("persistent", false, false),
            new WindowInfo(1200, 800, true, false));

        TomlManifestStore.SaveWebApp(path, manifest);
        var loaded = TomlManifestStore.LoadWebApp(path);

        Assert.Equal(manifest.Package, loaded.Package);
        Assert.Equal(manifest.Network, loaded.Network);
        Assert.Equal(manifest.Entry.Server, loaded.Entry.Server);
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
