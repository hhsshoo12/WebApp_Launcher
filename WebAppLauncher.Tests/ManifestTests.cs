using WebAppLauncher.Core;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

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
        Assert.Equal("new_backend", manifest.Window.InstanceMode);
    }

    [Fact]
    public void LoadFormat2WapkAllowsMissingVersion()
    {
        using var temp = new TempDirectory();
        var path = temp.Write(
            "app-v2.wapk",
            ValidWapk()
                .Replace("format = 1", "format = 2")
                .Replace("version = \"1.0\"\r\n", string.Empty)
                .Replace("version = \"1.0\"\n", string.Empty)
                .Replace("commit = \"abcdef1234567890\"", "commit = \"*\""));

        var manifest = TomlManifestStore.LoadWapk(path);

        Assert.Equal(2, manifest.Format);
        Assert.Equal(string.Empty, manifest.Package.Version);
        Assert.Equal("*", manifest.Source.Commit);
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
            new WindowInfo(
                1200,
                800,
                true,
                false,
                Transparent: true,
                Borderless: true,
                Fullscreen: false,
                AlwaysOnTop: true,
                StartMaximized: true,
                InstanceMode: "share_backend"));

        TomlManifestStore.SaveWebApp(path, manifest);
        var loaded = TomlManifestStore.LoadWebApp(path);

        Assert.Equal(manifest.Package, loaded.Package);
        Assert.Equal(manifest.Network, loaded.Network);
        Assert.Equal(manifest.Entry.Server, loaded.Entry.Server);
        Assert.Equal(manifest.Window, loaded.Window);
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
    public void LoadWapkAcceptsExtendedWindowOptions()
    {
        using var temp = new TempDirectory();
        var content = ValidWapk().Replace(
            "devtools = false",
            """
            devtools = false
            transparent = true
            borderless = true
            fullscreen = false
            always_on_top = true
            start_maximized = true
            instance_mode = "focus_existing"
            """);
        var path = temp.Write("window-options.wapk", content);

        var manifest = TomlManifestStore.LoadWapk(path);

        Assert.True(manifest.Window.Transparent);
        Assert.True(manifest.Window.Borderless);
        Assert.True(manifest.Window.AlwaysOnTop);
        Assert.True(manifest.Window.StartMaximized);
        Assert.Equal("focus_existing", manifest.Window.InstanceMode);
    }

    [Fact]
    public void LoadWapkRejectsUnknownInstanceMode()
    {
        using var temp = new TempDirectory();
        var path = temp.Write(
            "bad-instance-mode.wapk",
            ValidWapk().Replace(
                "devtools = false",
                """
                devtools = false
                instance_mode = "duplicate_magic"
                """));

        Assert.Throws<InvalidDataException>(() => TomlManifestStore.LoadWapk(path));
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
    public void AppUpdateApplyMovesPersistentDataAndRemovesOldVersion()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);
        paths.EnsureRootLayout();
        var oldApp = CreateInstalledApp(paths, "11111111", new string('1', 40));
        File.WriteAllText(Path.Combine(oldApp.DataDirectory, "state.json"), "saved");
        File.WriteAllText(Path.Combine(oldApp.LogDirectory, "app.log"), "log");

        var staging = Path.Combine(paths.AppUpdates, "owner@repo", new string('2', 40));
        var stagedApp = CreateInstalledAppAt(staging, "22222222", new string('2', 40));
        var prepared = new PreparedAppUpdate(
            "owner@repo",
            "11111111",
            "22222222",
            new string('2', 40),
            staging);

        var installed = new AppUpdateManager(paths).Apply(oldApp, prepared);

        Assert.Equal("22222222", installed.Manifest.Package.Version);
        Assert.Equal("saved", File.ReadAllText(Path.Combine(installed.DataDirectory, "state.json")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(installed.LogDirectory, "app.log")));
        Assert.False(Directory.Exists(oldApp.InstallDirectory));
        Assert.False(Directory.Exists(stagedApp.InstallDirectory));
    }

    [Fact]
    public async Task RuntimeUpdateDownloadVerifiesAndExtractsArchive()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);
        paths.EnsureRootLayout();
        var zip = CreateRuntimeZip();
        var checksum = Convert.ToHexString(SHA256.HashData(zip));
        using var client = new HttpClient(new StaticHttpHandler(new Dictionary<string, byte[]>
        {
            ["https://example.test/runtime.zip"] = zip,
            ["https://example.test/runtime.zip.sha256"] = Encoding.UTF8.GetBytes(checksum)
        }));
        var manager = new RuntimeUpdateManager(paths, client);
        var bundle = new RuntimeBundleInfo(
            "0.2",
            "https://example.test/runtime.zip",
            "https://example.test/runtime.zip.sha256",
            "available");

        var downloaded = await manager.DownloadAsync(bundle);

        Assert.Equal("downloaded", downloaded.Status);
        Assert.True(File.Exists(Path.Combine(downloaded.StagingDirectory!, "runtime-manifest.toml")));
        Assert.True(Directory.Exists(Path.Combine(downloaded.StagingDirectory!, "LICENSES")));
    }

    [Fact]
    public async Task RuntimeUpdateCheckTreatsMissingReleaseAsNoRelease()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);
        paths.EnsureRootLayout();
        using var client = new HttpClient(new StaticHttpHandler(new Dictionary<string, byte[]>()));
        var manager = new RuntimeUpdateManager(paths, client);

        var bundle = await manager.CheckAsync();

        Assert.Equal("no_release", bundle.Status);
        Assert.Equal("Runtime release was not found.", bundle.Message);
    }

    [Fact]
    public async Task RuntimeUpdateRejectsPathTraversalArchive()
    {
        using var temp = new TempDirectory();
        var paths = new WebAppPaths(temp.Path);
        paths.EnsureRootLayout();
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("../escape.txt");
        }

        var zip = memory.ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(zip));
        using var client = new HttpClient(new StaticHttpHandler(new Dictionary<string, byte[]>
        {
            ["https://example.test/bad.zip"] = zip,
            ["https://example.test/bad.zip.sha256"] = Encoding.UTF8.GetBytes(checksum)
        }));
        var manager = new RuntimeUpdateManager(paths, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync(
            new RuntimeBundleInfo(
                "bad",
                "https://example.test/bad.zip",
                "https://example.test/bad.zip.sha256",
                "available")));
    }

    [Fact]
    public async Task GitSourceResolverResolvesDefaultBranchLatestAndPinnedCommit()
    {
        using var temp = new TempDirectory();
        var remote = Path.Combine(temp.Path, "remote");
        Directory.CreateDirectory(remote);
        RunGit(remote, "init -b main");
        RunGit(remote, "config user.email test@example.invalid");
        RunGit(remote, "config user.name Test");
        File.WriteAllText(Path.Combine(remote, "app.html"), "one");
        RunGit(remote, "add app.html");
        RunGit(remote, "commit -m first");
        var first = RunGit(remote, "rev-parse HEAD");
        File.WriteAllText(Path.Combine(remote, "app.html"), "two");
        RunGit(remote, "commit -am second");
        var second = RunGit(remote, "rev-parse HEAD");

        var paths = new WebAppPaths(Path.Combine(temp.Path, "webapp-root"));
        paths.EnsureRootLayout();
        var resolver = new GitSourceResolver(paths, _ => remote);
        var latest = await resolver.ResolveAsync(
            new SourceInfo("github", "owner", "repo", "*", "*", "."));
        var pinned = await resolver.ResolveAsync(
            new SourceInfo("github", "owner", "repo", "main", first[..12], "."));

        Assert.Equal(second, latest.Commit);
        Assert.Equal("main", latest.Branch);
        Assert.Equal(first, pinned.Commit);
        Assert.Equal("two", File.ReadAllText(Path.Combine(latest.CheckoutDirectory, "app.html")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(pinned.CheckoutDirectory, "app.html")));
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

    private static InstalledApp CreateInstalledApp(WebAppPaths paths, string version, string commit)
    {
        return CreateInstalledAppAt(paths.GetAppDirectory("owner@repo", version), version, commit);
    }

    private static InstalledApp CreateInstalledAppAt(string directory, string version, string commit)
    {
        Directory.CreateDirectory(Path.Combine(directory, "source"));
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        Directory.CreateDirectory(Path.Combine(directory, "temp"));
        File.WriteAllText(Path.Combine(directory, "source", "app.html"), "<!doctype html>");
        var manifest = new WebAppManifest(
            2,
            DateTimeOffset.UtcNow,
            commit,
            new PackageInfo("owner@repo", "Test", version),
            new InstalledPaths("source", "data", "logs", "temp"),
            new RuntimeInfo("none", "none"),
            new EntryInfo("app.html", null, null, null, "static", null),
            new NetworkInfo("127.0.0.1", 0, "dynamic"),
            new StorageInfo("ephemeral", false, false),
            new WindowInfo(800, 600, true, false),
            new SourceInfo("github", "owner", "repo", "main", "*", "."));
        var manifestPath = Path.Combine(directory, "repo.webapp");
        TomlManifestStore.SaveWebApp(manifestPath, manifest);
        return new InstalledApp(manifest, directory, manifestPath);
    }

    private static byte[] CreateRuntimeZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("runtime-manifest.toml", "[runtime]\nbundle_version = \"0.2\"\n");
            Write("runtime/python313/python.exe", "python");
            Write("tools/git/git.exe", "git");
            Write("LICENSES/NOTICE.txt", "licenses");

            void Write(string path, string value)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(value);
            }
        }

        return memory.ToArray();
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(stderr);
        }

        return stdout.Trim();
    }
}

internal sealed class StaticHttpHandler(IReadOnlyDictionary<string, byte[]> responses)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is not null &&
            responses.TryGetValue(request.RequestUri.ToString(), out var content))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
