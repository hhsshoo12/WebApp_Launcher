using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using WebAppLauncher.Core;

namespace WebAppLauncher;

public partial class MainWindow : Window
{
    private const string UiHost = "launcher.webapp.local";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebAppPaths paths = new();
    private readonly AppRepository repository;
    private readonly AppInstaller installer;
    private readonly AppLauncher launcher;
    private readonly AppUpdateManager updateManager;
    private readonly RuntimeUpdateManager runtimeUpdateManager;
    private readonly LauncherSettingsStore settingsStore;
    private LauncherSettings settings;
    private readonly List<ActiveSession> activeSessions = [];
    private readonly Dictionary<string, AppUpdateStatus> updateStatuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PreparedAppUpdate> preparedUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    private RuntimeBundleInfo? runtimeBundle;

    public MainWindow()
    {
        repository = new AppRepository(paths);
        installer = new AppInstaller(paths);
        launcher = new AppLauncher(paths);
        updateManager = new AppUpdateManager(paths);
        runtimeUpdateManager = new RuntimeUpdateManager(paths);
        settingsStore = new LauncherSettingsStore(paths);
        settings = settingsStore.Load();
        foreach (var prepared in updateManager.ListPrepared())
        {
            preparedUpdates[prepared.PackageId] = prepared;
        }
        InitializeComponent();
        paths.EnsureRootLayout();
        Loaded += LoadedAsync;
    }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var profileDirectory = Path.Combine(paths.Root, "launcher-profile");
            Directory.CreateDirectory(profileDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory);
            await Browser.EnsureCoreWebView2Async(environment);

            var uiDirectory = Path.Combine(AppContext.BaseDirectory, "Ui");
            if (!File.Exists(Path.Combine(uiDirectory, "index.html")))
            {
                throw new FileNotFoundException("런처 UI 파일을 찾을 수 없습니다.", uiDirectory);
            }

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                UiHost,
                uiDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.CoreWebView2.WebMessageReceived += WebMessageReceived;
            Browser.Source = new Uri($"https://{UiHost}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                this,
                "Microsoft Edge WebView2 Runtime이 필요합니다. 설치 프로그램에서 전용 런타임 및 도구 설치를 실행하십시오.",
                "WebView2 Runtime 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "런처 시작 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        LauncherCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<LauncherCommand>(e.WebMessageAsJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            SendError(ex.Message);
            return;
        }

        if (command is null || string.IsNullOrWhiteSpace(command.Type))
        {
            SendError("잘못된 런처 명령입니다.");
            return;
        }

        try
        {
            switch (command.Type)
            {
                case "ready":
                    ApplyPreparedUpdatesForIdleApps();
                    SendState();
                    _ = RunScheduledChecksAsync();
                    break;
                case "refresh":
                    SendState("앱 목록을 새로 고쳤습니다.");
                    break;
                case "install":
                    await InstallAsync();
                    break;
                case "run":
                    Run(command);
                    break;
                case "remove":
                    Remove(command);
                    break;
                case "openData":
                    OpenData(command);
                    break;
                case "openLog":
                    OpenLog(command);
                    break;
                case "processManager":
                    SendProcessManagerState();
                    break;
                case "killProcess":
                    KillProcess(command);
                    break;
                case "doctor":
                    SendDoctor();
                    break;
                case "openRoot":
                    OpenFolder(paths.Root);
                    break;
                case "settings":
                    SendSettings();
                    break;
                case "setDeveloperMode":
                    SetDeveloperMode(command);
                    break;
                case "checkRuntimeUpdates":
                    await CheckRuntimeUpdatesAsync();
                    break;
                case "checkRuntimeRelease":
                    await CheckRuntimeReleaseAsync();
                    break;
                case "runtimeReleaseState":
                    Send(new { type = "runtimeRelease", status = "complete", bundle = runtimeBundle });
                    break;
                case "downloadRuntimeUpdate":
                    await DownloadRuntimeUpdateAsync();
                    break;
                case "applyRuntimeUpdate":
                    ApplyRuntimeUpdate();
                    break;
                case "checkAppUpdates":
                    await CheckAppUpdatesAsync(prepareAvailable: false);
                    break;
                case "appUpdateState":
                    SendAppUpdateState();
                    break;
                case "updateApp":
                    await UpdateAppAsync(command);
                    break;
                case "updateAllApps":
                    await UpdateAllAppsAsync();
                    break;
                case "setAutomaticAppUpdates":
                    SetAutomaticAppUpdates(command);
                    break;
                case "licenses":
                    SendLicenses();
                    break;
                default:
                    SendError($"지원하지 않는 명령입니다: {command.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendError(ex.Message);
        }
    }

    private async Task InstallAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "WebApp 패키지 (*.wapk)|*.wapk|모든 파일 (*.*)|*.*",
            Title = ".wapk 설치"
        };
        if (dialog.ShowDialog(this) != true)
        {
            Send(new { type = "idle" });
            return;
        }

        Send(new { type = "busy", message = "앱 소스와 의존성을 설치하는 중입니다." });
        try
        {
            var app = await installer.InstallAsync(dialog.FileName);
            SendState($"{app.Manifest.Package.Name} {app.Manifest.Package.Version}을 설치했습니다.");
        }
        catch
        {
            Send(new { type = "idle" });
            throw;
        }
    }

    private void Run(LauncherCommand command)
    {
        var app = ResolveApp(command);
        var matchingSessions = activeSessions
            .Where(session => IsSameApp(session.Launch.App, app))
            .ToArray();

        if (app.Manifest.Window.InstanceMode == "focus_existing" &&
            matchingSessions.FirstOrDefault() is { } existingSession)
        {
            BringToFront(existingSession);
            Send(new
            {
                type = "toast",
                tone = "success",
                message = $"{app.Manifest.Package.Name} 창을 앞으로 가져왔습니다."
            });
            return;
        }

        if (app.Manifest.Window.InstanceMode == "share_backend" &&
            matchingSessions.FirstOrDefault(session =>
                session.Launch.Process is null || !session.Launch.Process.HasExited) is { } sharedSession)
        {
            OpenAppWindow(sharedSession.Launch, ownsBackend: false);
            Send(new
            {
                type = "toast",
                tone = "success",
                message = $"{app.Manifest.Package.Name}의 새 창을 열었습니다."
            });
            return;
        }

        var result = launcher.Launch(app);
        AttachBackendExitHandler(result);
        OpenAppWindow(
            result,
            ownsBackend: app.Manifest.Window.InstanceMode != "share_backend");
        Send(new
        {
            type = "toast",
            tone = "success",
            message = $"{app.Manifest.Package.Name}을 실행했습니다."
        });
    }

    private void OpenAppWindow(LaunchResult result, bool ownsBackend)
    {
        var window = new AppWindow(result, settings.DeveloperMode, ownsBackend);
        var session = new ActiveSession(result, window);
        activeSessions.Add(session);

        window.Closed += (_, _) =>
        {
            if (result.App.Manifest.Window.InstanceMode == "share_backend" &&
                !activeSessions.Any(item =>
                    ReferenceEquals(item.Launch, result) &&
                    item.Window.IsVisible))
            {
                result.StopBackend();
            }

            SendProcessManagerState();
        };
        window.CleanupCompleted += (_, _) =>
        {
            activeSessions.Remove(session);
            try
            {
                ApplyPreparedUpdateIfIdle(result.App.Manifest.Package.Id);
            }
            catch (Exception ex)
            {
                SendError(ex.Message);
            }

            SendProcessManagerState();
        };
        window.Show();
        SendProcessManagerState();
    }

    private void AttachBackendExitHandler(LaunchResult result)
    {
        if (result.Process is not { } process)
        {
            return;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var session in activeSessions
                             .Where(item => ReferenceEquals(item.Launch, result))
                             .ToArray())
                {
                    if (session.Window.IsVisible)
                    {
                        session.Window.Close();
                    }
                }

                SendProcessManagerState();
            });
    }

    private static void BringToFront(ActiveSession session)
    {
        var window = session.Window;
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState =
                session.Launch.App.Manifest.Window.Fullscreen ||
                session.Launch.App.Manifest.Window.StartMaximized
                    ? WindowState.Maximized
                    : WindowState.Normal;
        }

        var wasTopmost = window.Topmost;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = wasTopmost;
    }

    private void Remove(LauncherCommand command)
    {
        var app = ResolveApp(command);
        installer.Remove(app.Manifest.Package.Id, app.Manifest.Package.Version);
        preparedUpdates.Remove(app.Manifest.Package.Id);
        updateStatuses.Remove(app.Manifest.Package.Id);
        SendState($"{app.Manifest.Package.Name} {app.Manifest.Package.Version}을 삭제했습니다.");
    }

    private void OpenData(LauncherCommand command)
    {
        var app = ResolveApp(command);
        Directory.CreateDirectory(app.DataDirectory);
        OpenFolder(app.DataDirectory);
    }

    private void OpenLog(LauncherCommand command)
    {
        var app = ResolveApp(command);
        Directory.CreateDirectory(app.LogDirectory);
        OpenFolder(app.LogDirectory);
    }

    private InstalledApp ResolveApp(LauncherCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.PackageId) || string.IsNullOrWhiteSpace(command.Version))
        {
            throw new InvalidOperationException("앱 식별 정보가 없습니다.");
        }

        return repository.GetInstalled(command.PackageId, command.Version);
    }

    private void SendState(string? notification = null)
    {
        var apps = repository.ListInstalled()
            .Select(app => new
            {
                packageId = app.Manifest.Package.Id,
                name = app.Manifest.Package.Name,
                version = app.Manifest.Package.Version,
                runtime = DescribeRuntime(app.Manifest.Runtime),
                mode = app.Manifest.Entry.Mode ?? "static",
                port = app.Manifest.Entry.Mode == "server" ? "자동" : "없음",
                origin = app.Manifest.Entry.Mode == "server" ? "실행 시 할당" : "로컬 파일",
                installedAt = app.Manifest.InstalledAt,
                installDirectory = app.InstallDirectory,
                icon = ReadIconDataUri(app),
                update = UpdateStateFor(app)
            })
            .ToArray();

        Send(new
        {
            type = "state",
            root = paths.Root,
            apps,
            notification
        });
    }

    private void KillProcess(LauncherCommand command)
    {
        if (command.ProcessId is null)
        {
            throw new InvalidOperationException("프로세스 식별 정보가 없습니다.");
        }

        var session = activeSessions.FirstOrDefault(s =>
            s.Launch.Process?.Id == command.ProcessId &&
            (string.IsNullOrWhiteSpace(command.PackageId) ||
             s.Launch.App.Manifest.Package.Id.Equals(command.PackageId, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(command.Version) ||
             s.Launch.App.Manifest.Package.Version.Equals(command.Version, StringComparison.OrdinalIgnoreCase)));
        if (session is null)
        {
            throw new InvalidOperationException("실행 중인 프로세스를 찾을 수 없습니다.");
        }

        foreach (var matchingSession in activeSessions
                     .Where(item => ReferenceEquals(item.Launch, session.Launch))
                     .ToArray())
        {
            matchingSession.Window.Close();
        }

        session.Launch.StopBackend();
        Send(new { type = "idle" });
    }

    private void SendProcessManagerState()
    {
        var occupiedPorts = PortManager.GetOccupiedPorts();
        var processes = activeSessions
            .Where(s => s.Launch.Process is { HasExited: false })
            .DistinctBy(s => s.Launch.Process!.Id)
            .Select(s => new
            {
                packageId = s.Launch.App.Manifest.Package.Id,
                name = s.Launch.App.Manifest.Package.Name,
                version = s.Launch.App.Manifest.Package.Version,
                runtime = DescribeRuntime(s.Launch.App.Manifest.Runtime),
                mode = s.Launch.App.Manifest.Entry.Mode ?? "static",
                port = s.Launch.Port,
                processId = s.Launch.Process!.Id,
                processName = s.Launch.Process.ProcessName,
                logPath = s.Launch.LogPath
            })
            .ToArray();

        Send(new
        {
            type = "processManager",
            ports = new
            {
                occupied = occupiedPorts.Count,
                total = PortManager.LastPort - PortManager.FirstPort + 1,
                percent = occupiedPorts.Count / 10.0,
                values = occupiedPorts
            },
            processes
        });
    }

    private void SendDoctor()
    {
        Send(new
        {
            type = "doctor",
            items = new ToolResolver(paths).Doctor()
        });
    }

    private void SendSettings()
    {
        Send(new
        {
            type = "settings",
            developerMode = settings.DeveloperMode,
            automaticAppUpdates = settings.AutomaticAppUpdates,
            lastAppUpdateCheck = settings.LastAppUpdateCheck,
            lastRuntimeUpdateCheck = settings.LastRuntimeUpdateCheck
        });
    }

    private void SetDeveloperMode(LauncherCommand command)
    {
        if (command.Enabled is null)
        {
            throw new InvalidOperationException("개발 모드 설정값이 없습니다.");
        }

        settings = settings with { DeveloperMode = command.Enabled.Value };
        settingsStore.Save(settings);
        SendSettings();
        Send(new
        {
            type = "toast",
            tone = "success",
            message = settings.DeveloperMode
                ? "개발 모드를 켰습니다. 새로 실행하는 앱에서 WebView 콘솔이 열립니다."
                : "개발 모드를 껐습니다."
        });
    }

    private async Task CheckRuntimeUpdatesAsync()
    {
        Send(new { type = "runtimeCheck", status = "checking" });
        var items = await new RuntimeInspector(paths).InspectAsync();
        Send(new { type = "runtimeCheck", status = "complete", items });
    }

    private async Task RunScheduledChecksAsync()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (settings.AutomaticAppUpdates &&
                now - settings.LastAppUpdateCheck.GetValueOrDefault(DateTimeOffset.MinValue) >= TimeSpan.FromDays(1))
            {
                await CheckAppUpdatesAsync(prepareAvailable: true, quiet: true);
            }

            if (now - settings.LastRuntimeUpdateCheck.GetValueOrDefault(DateTimeOffset.MinValue) >= TimeSpan.FromDays(1))
            {
                await CheckRuntimeReleaseAsync(quiet: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task CheckAppUpdatesAsync(bool prepareAvailable, bool quiet = false)
    {
        if (!quiet)
        {
            Send(new { type = "appUpdates", status = "checking" });
        }

        foreach (var app in repository.ListInstalled())
        {
            var status = await updateManager.CheckAsync(app);
            updateStatuses[app.Manifest.Package.Id] = status;
            if (prepareAvailable && status.Status == "available")
            {
                try
                {
                    var prepared = await updateManager.PrepareAsync(app);
                    if (prepared is not null)
                    {
                        preparedUpdates[prepared.PackageId] = prepared;
                        updateStatuses[prepared.PackageId] = status with { Status = "pending" };
                        ApplyPreparedUpdateIfIdle(prepared.PackageId);
                    }
                }
                catch (Exception ex)
                {
                    updateStatuses[app.Manifest.Package.Id] =
                        status with { Status = "error", Message = ex.Message };
                }
            }
        }

        settings = settings with { LastAppUpdateCheck = DateTimeOffset.UtcNow };
        settingsStore.Save(settings);
        SendState();
        SendAppUpdateState();
    }

    private async Task UpdateAppAsync(LauncherCommand command)
    {
        var app = ResolveApp(command);
        Send(new { type = "busy", message = $"{app.Manifest.Package.Name} 업데이트를 준비하는 중입니다." });
        var prepared = await updateManager.PrepareAsync(app);
        if (prepared is null)
        {
            updateStatuses[app.Manifest.Package.Id] = await updateManager.CheckAsync(app);
            SendState($"{app.Manifest.Package.Name}은 이미 최신 버전입니다.");
            SendAppUpdateState();
            return;
        }

        preparedUpdates[prepared.PackageId] = prepared;
        updateStatuses[prepared.PackageId] = new AppUpdateStatus(
            prepared.PackageId,
            app.Manifest.Package.Name,
            prepared.OldVersion,
            app.Manifest.SourceCommit,
            prepared.NewVersion,
            prepared.Commit,
            "pending");
        var applied = ApplyPreparedUpdateIfIdle(prepared.PackageId);
        SendState(applied
            ? $"{app.Manifest.Package.Name}을 {prepared.NewVersion} 버전으로 업데이트했습니다."
            : $"{app.Manifest.Package.Name} 업데이트를 준비했습니다. 앱 종료 후 적용됩니다.");
        SendAppUpdateState();
    }

    private async Task UpdateAllAppsAsync()
    {
        foreach (var app in repository.ListInstalled())
        {
            if (app.Manifest.Format != 2 || app.Manifest.Source?.Commit != "*")
            {
                continue;
            }

            try
            {
                var prepared = await updateManager.PrepareAsync(app);
                if (prepared is not null)
                {
                    preparedUpdates[prepared.PackageId] = prepared;
                    ApplyPreparedUpdateIfIdle(prepared.PackageId);
                }
            }
            catch (Exception ex)
            {
                updateStatuses[app.Manifest.Package.Id] = new AppUpdateStatus(
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

        await CheckAppUpdatesAsync(prepareAvailable: false);
    }

    private void SetAutomaticAppUpdates(LauncherCommand command)
    {
        if (command.Enabled is null)
        {
            throw new InvalidOperationException("자동 업데이트 설정값이 없습니다.");
        }

        settings = settings with { AutomaticAppUpdates = command.Enabled.Value };
        settingsStore.Save(settings);
        SendSettings();
    }

    private void SendAppUpdateState()
    {
        Send(new
        {
            type = "appUpdates",
            status = "complete",
            lastChecked = settings.LastAppUpdateCheck,
            items = updateStatuses.Values.OrderBy(item => item.Name).ToArray()
        });
    }

    private bool ApplyPreparedUpdateIfIdle(string packageId)
    {
        if (!preparedUpdates.TryGetValue(packageId, out var prepared) ||
            activeSessions.Any(session =>
                session.Launch.App.Manifest.Package.Id.Equals(
                    packageId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var oldApp = repository.GetInstalled(packageId, prepared.OldVersion);
        var installed = updateManager.Apply(oldApp, prepared);
        preparedUpdates.Remove(packageId);
        updateStatuses[packageId] = new AppUpdateStatus(
            packageId,
            installed.Manifest.Package.Name,
            installed.Manifest.Package.Version,
            installed.Manifest.SourceCommit,
            installed.Manifest.Package.Version,
            installed.Manifest.SourceCommit,
            "current");
        SendState();
        return true;
    }

    private void ApplyPreparedUpdatesForIdleApps()
    {
        foreach (var packageId in preparedUpdates.Keys.ToArray())
        {
            try
            {
                ApplyPreparedUpdateIfIdle(packageId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }

    private object? UpdateStateFor(InstalledApp app)
    {
        if (preparedUpdates.TryGetValue(app.Manifest.Package.Id, out var prepared))
        {
            return new
            {
                status = "pending",
                latestVersion = prepared.NewVersion,
                latestCommit = prepared.Commit
            };
        }

        return updateStatuses.TryGetValue(app.Manifest.Package.Id, out var status)
            ? new
            {
                status = status.Status,
                latestVersion = status.LatestVersion,
                latestCommit = status.LatestCommit,
                message = status.Message
            }
            : null;
    }

    private async Task CheckRuntimeReleaseAsync(bool quiet = false)
    {
        if (!quiet)
        {
            Send(new { type = "runtimeRelease", status = "checking" });
        }

        runtimeBundle = await runtimeUpdateManager.CheckAsync();
        settings = settings with { LastRuntimeUpdateCheck = DateTimeOffset.UtcNow };
        settingsStore.Save(settings);
        Send(new { type = "runtimeRelease", status = "complete", bundle = runtimeBundle });
    }

    private async Task DownloadRuntimeUpdateAsync()
    {
        runtimeBundle ??= await runtimeUpdateManager.CheckAsync();
        if (runtimeBundle.Status == "error")
        {
            throw new InvalidOperationException(runtimeBundle.Message);
        }

        var progress = new Progress<double>(value =>
            Send(new { type = "runtimeDownload", percent = Math.Round(value, 1) }));
        runtimeBundle = await runtimeUpdateManager.DownloadAsync(runtimeBundle, progress);
        Send(new { type = "runtimeRelease", status = "complete", bundle = runtimeBundle });
    }

    private void ApplyRuntimeUpdate()
    {
        if (runtimeBundle?.Status != "downloaded" ||
            string.IsNullOrWhiteSpace(runtimeBundle.StagingDirectory))
        {
            throw new InvalidOperationException("다운로드된 런타임 업데이트가 없습니다.");
        }

        foreach (var session in activeSessions.ToArray())
        {
            session.Window.Close();
        }

        var bootstrapper = Path.Combine(AppContext.BaseDirectory, "WebAppLauncher.Bootstrapper.exe");
        if (!File.Exists(bootstrapper))
        {
            throw new FileNotFoundException("Bootstrapper를 찾을 수 없습니다.", bootstrapper);
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("런처 실행 파일 경로를 확인할 수 없습니다.");
        Process.Start(new ProcessStartInfo(
            bootstrapper,
            $"apply-runtime --staging {Quote(runtimeBundle.StagingDirectory)} " +
            $"--wait-pid {Environment.ProcessId} --restart {Quote(executable)} --root {Quote(paths.Root)}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Application.Current.Shutdown();
    }

    private void SendLicenses()
    {
        Send(new
        {
            type = "licenses",
            project = ReadDistributionText("LICENSE"),
            thirdParty = ReadDistributionText("THIRD_PARTY_NOTICES.md")
        });
    }

    private static string ReadDistributionText(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : "라이선스 문서를 찾을 수 없습니다.";
    }

    private void SendError(string message)
    {
        Send(new { type = "error", message });
    }

    private void Send(object payload)
    {
        Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string DescribeRuntime(RuntimeInfo runtime)
    {
        var values = new[] { runtime.Python, runtime.Node }
            .Where(value => !value.Equals("none", StringComparison.OrdinalIgnoreCase));
        return string.Join(" + ", values);
    }

    private static bool IsSameApp(InstalledApp left, InstalledApp right)
    {
        return left.Manifest.Package.Id.Equals(
                   right.Manifest.Package.Id,
                   StringComparison.OrdinalIgnoreCase) &&
               left.Manifest.Package.Version.Equals(
                   right.Manifest.Package.Version,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string? ReadIconDataUri(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.Manifest.Entry.Icon))
        {
            return null;
        }

        var iconPath = Path.GetFullPath(Path.Combine(app.SourceDirectory, app.Manifest.Entry.Icon));
        var sourceRoot = Path.GetFullPath(app.SourceDirectory) + Path.DirectorySeparatorChar;
        if (!iconPath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(iconPath))
        {
            return null;
        }

        var extension = Path.GetExtension(iconPath).ToLowerInvariant();
        var mime = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null
        };
        if (mime is null || new FileInfo(iconPath).Length > 2 * 1024 * 1024)
        {
            return null;
        }

        return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(iconPath))}";
    }

    private static void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
        {
            UseShellExecute = true
        });
    }

    private sealed record LauncherCommand(
        string Type,
        string? PackageId = null,
        string? Version = null,
        bool? Enabled = null,
        int? ProcessId = null);

    private sealed record ActiveSession(LaunchResult Launch, AppWindow Window);
}
