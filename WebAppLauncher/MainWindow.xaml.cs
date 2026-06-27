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
    private readonly AppUpdateCoordinator appUpdates;
    private readonly LauncherSettingsStore settingsStore;
    private readonly InstallStateStore installStateStore;
    private readonly LauncherUpdateManager launcherUpdateManager;
    private readonly AppSessionManager sessions = new();
    private readonly LauncherCommandDispatcher commandDispatcher;
    private LauncherSettings settings;
    private LauncherReleaseInfo? launcherUpdate;

    public MainWindow()
    {
        repository = new AppRepository(paths);
        installer = new AppInstaller(paths);
        launcher = new AppLauncher(paths);
        appUpdates = new AppUpdateCoordinator(repository, new AppUpdateManager(paths));
        settingsStore = new LauncherSettingsStore(paths);
        installStateStore = InstallStateStore.ForDirectory(AppContext.BaseDirectory);
        launcherUpdateManager = new LauncherUpdateManager(installStateStore);
        settings = settingsStore.Load();

        commandDispatcher = CreateCommandDispatcher();
        InitializeComponent();
        paths.EnsureRootLayout();
        Loaded += LoadedAsync;
    }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = Task.Run(() => CleanupOldTempProfiles());

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
            Browser.CoreWebView2.NavigationStarting += LauncherNavigationStarting;
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
        if (!IsTrustedLauncherSource(e.Source))
        {
            SendError("신뢰할 수 없는 런처 UI 메시지를 차단했습니다.");
            return;
        }

        LauncherCommand? command;
        try
        {
            command = LauncherCommandDispatcher.Deserialize(e.WebMessageAsJson, JsonOptions);
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
            await commandDispatcher.DispatchAsync(command);
        }
        catch (Exception ex)
        {
            SendError(ex.Message);
        }
    }

    private LauncherCommandDispatcher CreateCommandDispatcher()
    {
        return LauncherCommandDispatcher.CreateBuilder()
            .On("ready", command =>
            {
                ApplyPreparedUpdatesForIdleApps();
                SendState();
                _ = RunScheduledChecksAsync();
            })
            .On("refresh", _ => SendState("앱 목록을 새로 고쳤습니다."))
            .OnAsync("install", _ => InstallAsync())
            .On("run", Run)
            .On("remove", Remove)
            .On("openData", OpenData)
            .On("openLog", OpenLog)
            .On("processManager", _ => SendProcessManagerState())
            .On("killProcess", KillProcess)
            .On("doctor", _ => SendDoctor())
            .On("openRoot", _ => OpenFolder(paths.Root))
            .On("settings", _ => SendSettings())
            .On("setDeveloperMode", SetDeveloperMode)
            .OnAsync("checkRuntimeStatus", _ => CheckRuntimeStatusAsync())
            .OnAsync("checkAppUpdates", _ => CheckAppUpdatesAsync(prepareAvailable: false))
            .On("appUpdateState", _ => SendAppUpdateState())
            .OnAsync("updateApp", UpdateAppAsync)
            .OnAsync("updateAllApps", _ => UpdateAllAppsAsync())
            .On("setAutomaticAppUpdates", SetAutomaticAppUpdates)
            .OnAsync("checkLauncherUpdate", _ => CheckLauncherUpdateAsync())
            .On("runLauncherUpdate", _ => RunLauncherUpdate())
            .On("licenses", _ => SendLicenses())
            .Build();
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
            OfferDesktopShortcut(app);
        }
        catch
        {
            Send(new { type = "idle" });
            throw;
        }
    }

    private void OfferDesktopShortcut(InstalledApp app)
    {
        var result = MessageBox.Show(
            this,
            $"{app.Manifest.Package.Name}의 바탕화면 바로가기를 만들까요?",
            "바로가기 만들기",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var launcherPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("런처 경로를 확인할 수 없습니다.");
            var shortcutService = new AppShortcutService();
            var shortcutPath = shortcutService.CreateDesktopShortcut(app, launcherPath);
            Send(new
            {
                type = "toast",
                tone = "success",
                message = $"바탕화면 바로가기를 만들었습니다: {shortcutPath}"
            });
        }
        catch (Exception ex)
        {
            Send(new
            {
                type = "toast",
                tone = "error",
                message = $"바로가기 만들기 실패: {ex.Message}"
            });
        }
    }

    private void Run(LauncherCommand command)
    {
        var app = ResolveApp(command);

        if (app.Manifest.Window.InstanceMode == "focus_existing" &&
            sessions.FindFirstApp(app) is { } existingSession)
        {
            AppSessionManager.BringToFront(existingSession);
            Send(new
            {
                type = "toast",
                tone = "success",
                message = $"{app.Manifest.Package.Name} 창을 앞으로 가져왔습니다."
            });
            return;
        }

        if (app.Manifest.Window.InstanceMode == "share_backend" &&
            sessions.FindReusableBackend(app) is { } sharedSession)
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
        var session = sessions.Add(result, window);

        window.Closed += (_, _) =>
        {
            if (result.App.Manifest.Window.InstanceMode == "share_backend" &&
                !sessions.HasVisibleWindowForLaunch(result))
            {
                result.StopBackend();
            }

            SendProcessManagerState();
        };
        window.CleanupCompleted += (_, _) =>
        {
            sessions.Remove(session);
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
                foreach (var session in sessions.ForLaunch(result))
                {
                    if (session.Window.IsVisible)
                    {
                        session.Window.Close();
                    }
                }

                SendProcessManagerState();
            });
    }

    private void Remove(LauncherCommand command)
    {
        var app = ResolveApp(command);
        if (sessions.HasApp(app))
        {
            throw new InvalidOperationException("실행 중인 앱은 종료한 뒤 삭제할 수 있습니다.");
        }

        installer.Remove(app.Manifest.Package.Id, app.Manifest.Package.Version);
        appUpdates.RemovePackage(app.Manifest.Package.Id);
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
            launcherVersion = LauncherVersion.Current,
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

        sessions.CloseWindowsAndStopBackend(command.ProcessId.Value, command.PackageId, command.Version);
        Send(new { type = "idle" });
    }

    private void SendProcessManagerState()
    {
        var state = sessions.GetProcessManagerState();

        Send(new
        {
            type = "processManager",
            ports = state.Ports,
            processes = state.Processes
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
            lastAppUpdateCheck = settings.LastAppUpdateCheck
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

    private async Task CheckRuntimeStatusAsync()
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

        await appUpdates.CheckInstalledAsync(
            prepareAvailable,
            packageId => !sessions.HasPackage(packageId));

        settings = settings with { LastAppUpdateCheck = DateTimeOffset.UtcNow };
        settingsStore.Save(settings);
        SendState();
        SendAppUpdateState();
    }

    private async Task UpdateAppAsync(LauncherCommand command)
    {
        var app = ResolveApp(command);
        Send(new { type = "busy", message = $"{app.Manifest.Package.Name} 업데이트를 준비하는 중입니다." });
        var result = await appUpdates.PrepareAsync(
            app,
            packageId => !sessions.HasPackage(packageId));
        if (result is null)
        {
            SendState($"{app.Manifest.Package.Name}은 이미 최신 버전입니다.");
            SendAppUpdateState();
            return;
        }

        SendState(result.Applied
            ? $"{app.Manifest.Package.Name}을 {result.Prepared.NewVersion} 버전으로 업데이트했습니다."
            : $"{app.Manifest.Package.Name} 업데이트를 준비했습니다. 앱 종료 후 적용됩니다.");
        SendAppUpdateState();
    }

    private async Task UpdateAllAppsAsync()
    {
        await appUpdates.PrepareAllAsync(packageId => !sessions.HasPackage(packageId));

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

    private async Task CheckLauncherUpdateAsync()
    {
        Send(new { type = "launcherUpdate", status = "checking" });
        launcherUpdate = await launcherUpdateManager.CheckAsync();
        Send(new { type = "launcherUpdate", status = "complete", update = launcherUpdate });
    }

    private void RunLauncherUpdate()
    {
        var state = launcherUpdateManager.GetInstallState();
        if (state is null)
        {
            SendError("설치된 런처의 상태 파일을 찾을 수 없습니다.");
            return;
        }

        try
        {
            launcherUpdateManager.TriggerUpdate(state);
        }
        catch (FileNotFoundException ex)
        {
            SendError(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            SendError($"업데이트를 시작할 수 없습니다: {ex.Message}");
            return;
        }

        SendState("런처 업데이트를 시작했습니다. 잠시 후 새 버전이 실행됩니다.");
        sessions.CloseAll();
        Application.Current.Shutdown();
    }

    private void SendAppUpdateState()
    {
        Send(new
        {
            type = "appUpdates",
            status = "complete",
            lastChecked = settings.LastAppUpdateCheck,
            items = appUpdates.Statuses.OrderBy(item => item.Name).ToArray()
        });
    }

    private bool ApplyPreparedUpdateIfIdle(string packageId)
    {
        var applied = appUpdates.ApplyIfIdle(packageId, id => !sessions.HasPackage(id));
        if (applied)
        {
            SendState();
        }

        return applied;
    }

    private void ApplyPreparedUpdatesForIdleApps()
    {
        foreach (var packageId in repository.ListInstalled()
                     .Select(app => app.Manifest.Package.Id)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToArray())
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
        return appUpdates.StateFor(app);
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

    private static void LauncherNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsTrustedLauncherSource(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static bool IsTrustedLauncherSource(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals(UiHost, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeRuntime(RuntimeInfo runtime)
    {
        var values = new[] { runtime.Python, runtime.Node }
            .Where(value => !value.Equals("none", StringComparison.OrdinalIgnoreCase));
        return string.Join(" + ", values);
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
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    private void CleanupOldTempProfiles()
    {
        try
        {
            var tempBase = Path.Combine(Path.GetTempPath(), "WebAppLauncher", "webview");
            if (Directory.Exists(tempBase))
            {
                foreach (var dir in Directory.EnumerateDirectories(tempBase))
                {
                    var name = Path.GetFileName(dir);
                    if (Guid.TryParse(name, out _))
                    {
                        try
                        {
                            Directory.Delete(dir, recursive: true);
                        }
                        catch
                        {
                            // Ignored
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignored
        }
    }
}
