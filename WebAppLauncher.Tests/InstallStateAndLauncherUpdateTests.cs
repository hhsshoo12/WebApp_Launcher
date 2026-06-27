using System.Net;
using System.Text;
using System.Text.Json;
using WebAppLauncher.Core;
using Xunit;

namespace WebAppLauncher.Tests;

public sealed class InstallStateStoreTests
{
    [Fact]
    public void SaveAndLoadRoundTripsInstallState()
    {
        using var temp = new TempDirectory();
        var directory = System.IO.Path.Combine(temp.Path, "WebAppLauncher");
        Directory.CreateDirectory(directory);
        var store = InstallStateStore.ForDirectory(directory);
        var expected = new InstallState(
            InstallState.CurrentFormat,
            "WebApp Launcher",
            "0.1.0",
            directory,
            System.IO.Path.Combine(temp.Path, "WebAppLauncher-Setup.exe"));

        store.Save(expected);
        var actual = store.Load();

        Assert.NotNull(actual);
        Assert.Equal(InstallState.CurrentFormat, actual!.Format);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.InstallLocation, actual.InstallLocation);
        Assert.Equal(expected.SetupPath, actual.SetupPath);
    }

    [Fact]
    public void LoadReturnsNullWhenFileMissing()
    {
        using var temp = new TempDirectory();
        var store = InstallStateStore.ForDirectory(temp.Path);
        Assert.Null(store.Load());
    }

    [Fact]
    public void LoadReturnsNullForCorruptJson()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, InstallState.FileName);
        File.WriteAllText(path, "not json");
        var store = new InstallStateStore(path);
        Assert.Null(store.Load());
    }

    [Fact]
    public void SaveCreatesTempFileAndAtomicallyReplaces()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, InstallState.FileName);
        var store = new InstallStateStore(path);
        var expected = new InstallState(InstallState.CurrentFormat, "WebApp Launcher", "0.2.0", temp.Path);

        store.Save(expected);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SaveEmitsSnakeCaseKeysMatchingPythonInstaller()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, InstallState.FileName);
        var store = new InstallStateStore(path);
        var expected = new InstallState(
            InstallState.CurrentFormat,
            "WebApp Launcher",
            "0.1.0",
            temp.Path,
            System.IO.Path.Combine(temp.Path, "WebAppLauncher-Setup.exe"));

        store.Save(expected);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("format").GetInt32());
        Assert.Equal("WebApp Launcher", root.GetProperty("product").GetString());
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.Equal(temp.Path, root.GetProperty("install_location").GetString());
        Assert.Equal(
            System.IO.Path.Combine(temp.Path, "WebAppLauncher-Setup.exe"),
            root.GetProperty("setup_path").GetString());
    }

    [Fact]
    public void LoadReadsSnakeCaseKeysWrittenByPythonInstaller()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, InstallState.FileName);
        File.WriteAllText(path, """
            {
              "format": 2,
              "product": "WebApp Launcher",
              "version": "0.1.0",
              "install_location": "C:\\Program Files\\WebAppLauncher",
              "setup_path": "C:\\Users\\me\\.wapk\\bootstrapper\\WebAppLauncher-Setup.exe"
            }
            """);
        var store = new InstallStateStore(path);

        var state = store.Load();

        Assert.NotNull(state);
        Assert.Equal(2, state!.Format);
        Assert.Equal("0.1.0", state.Version);
        Assert.Equal("C:\\Program Files\\WebAppLauncher", state.InstallLocation);
        Assert.Equal(
            "C:\\Users\\me\\.wapk\\bootstrapper\\WebAppLauncher-Setup.exe",
            state.SetupPath);
    }
}

public sealed class LauncherUpdateManagerTests
{
    [Fact]
    public async Task CheckAsyncReturnsNoStateWhenInstallStateMissing()
    {
        using var temp = new TempDirectory();
        var store = InstallStateStore.ForDirectory(temp.Path);
        var manager = new LauncherUpdateManager(store);

        var result = await manager.CheckAsync();

        Assert.Equal("no_state", result.Status);
        Assert.Equal(string.Empty, result.InstalledVersion);
        Assert.Equal(string.Empty, result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsyncPicksLatestVLauncherReleaseAndSkipsRuntimeAndDrafts()
    {
        using var temp = new TempDirectory();
        var store = InstallStateStore.ForDirectory(temp.Path);
        store.Save(new InstallState(InstallState.CurrentFormat, "WebApp Launcher", "0.1.0", temp.Path));

        var releasePayload = $$"""
        [
          {"tag_name":"runtime-v0.1","draft":false,"published_at":"2026-06-30T00:00:00Z","assets":[]},
          {"tag_name":"v0.1.0","draft":true,"published_at":"2026-06-29T00:00:00Z","assets":[]},
          {"tag_name":"v0.3.0","draft":false,"published_at":"2026-06-28T00:00:00Z","assets":[
            {"name":"WAPL-Launcher-v0.3.0.zip","browser_download_url":"https://example/zip-3"},
            {"name":"WAPL-Launcher-v0.3.0.zip.sha256","browser_download_url":"https://example/sha-3"}
          ]}
        ]
        """;
        var handler = new ScriptedHttpHandler(releasePayload);
        using var client = new HttpClient(handler);
        var manager = new LauncherUpdateManager(store, client);

        var result = await manager.CheckAsync();

        Assert.Equal("available", result.Status);
        Assert.Equal("0.1.0", result.InstalledVersion);
        Assert.Equal("0.3.0", result.LatestVersion);
        Assert.Equal("https://example/zip-3", result.DownloadUrl);
        Assert.Equal("https://example/sha-3", result.ChecksumUrl);
        Assert.Single(handler.Requests);
        Assert.Contains("/releases?", handler.Requests[0]);
    }

    [Fact]
    public async Task CheckAsyncReportsCurrentWhenInstalledMatchesLatest()
    {
        using var temp = new TempDirectory();
        var store = InstallStateStore.ForDirectory(temp.Path);
        store.Save(new InstallState(InstallState.CurrentFormat, "WebApp Launcher", "0.4.0", temp.Path));

        var releasePayload = """
        [
          {"tag_name":"v0.4.0","draft":false,"published_at":"2026-06-26T00:00:00Z","assets":[
            {"name":"WAPL-Launcher-v0.4.0.zip","browser_download_url":"https://example/zip"},
            {"name":"WAPL-Launcher-v0.4.0.zip.sha256","browser_download_url":"https://example/sha"}
          ]}
        ]
        """;
        var handler = new ScriptedHttpHandler(releasePayload);
        using var client = new HttpClient(handler);
        var manager = new LauncherUpdateManager(store, client);

        var result = await manager.CheckAsync();

        Assert.Equal("current", result.Status);
        Assert.Equal("0.4.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsyncReturnsErrorWhenHttpFails()
    {
        using var temp = new TempDirectory();
        var store = InstallStateStore.ForDirectory(temp.Path);
        store.Save(new InstallState(InstallState.CurrentFormat, "WebApp Launcher", "0.1.0", temp.Path));

        var handler = new ScriptedHttpHandler(null, status: HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var manager = new LauncherUpdateManager(store, client);

        var result = await manager.CheckAsync();

        Assert.Equal("error", result.Status);
        Assert.Equal("0.1.0", result.InstalledVersion);
    }

    [Fact]
    public async Task CheckAsyncReturnsNoReleaseWhenNoAssets()
    {
        using var temp = new TempDirectory();
        var store = InstallStateStore.ForDirectory(temp.Path);
        store.Save(new InstallState(InstallState.CurrentFormat, "WebApp Launcher", "0.1.0", temp.Path));

        var releasePayload = """
        [{"tag_name":"v0.5.0","draft":false,"published_at":"2026-06-26T00:00:00Z","assets":[]}]
        """;
        var handler = new ScriptedHttpHandler(releasePayload);
        using var client = new HttpClient(handler);
        var manager = new LauncherUpdateManager(store, client);

        var result = await manager.CheckAsync();

        Assert.Equal("no_release", result.Status);
    }

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly string? body;
        private readonly HttpStatusCode status;

        public ScriptedHttpHandler(string? body, HttpStatusCode status = HttpStatusCode.OK)
        {
            this.body = body;
            this.status = status;
        }

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            var response = new HttpResponseMessage(status)
            {
                Content = body is null
                    ? new StringContent(string.Empty, Encoding.UTF8, "application/json")
                    : new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
