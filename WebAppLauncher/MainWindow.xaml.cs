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
    private readonly LauncherSettingsStore settingsStore;
    private LauncherSettings settings;

    public MainWindow()
    {
        repository = new AppRepository(paths);
        installer = new AppInstaller(paths);
        launcher = new AppLauncher(paths);
        settingsStore = new LauncherSettingsStore(paths);
        settings = settingsStore.Load();
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
                SendState();
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
        var result = launcher.Launch(app);
        var window = new AppWindow(result, settings.DeveloperMode);
        window.Show();
        Send(new
        {
            type = "toast",
            tone = "success",
            message = $"{app.Manifest.Package.Name}을 실행했습니다."
        });
    }

    private void Remove(LauncherCommand command)
    {
        var app = ResolveApp(command);
        installer.Remove(app.Manifest.Package.Id, app.Manifest.Package.Version);
        SendState($"{app.Manifest.Package.Name} {app.Manifest.Package.Version}을 삭제했습니다.");
    }

    private void OpenData(LauncherCommand command)
    {
        var app = ResolveApp(command);
        Directory.CreateDirectory(app.DataDirectory);
        OpenFolder(app.DataDirectory);
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
                icon = ReadIconDataUri(app)
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
        var occupiedPorts = PortManager.GetOccupiedPorts();
        Send(new
        {
            type = "settings",
            developerMode = settings.DeveloperMode,
            ports = new
            {
                occupied = occupiedPorts.Count,
                total = PortManager.LastPort - PortManager.FirstPort + 1,
                percent = occupiedPorts.Count / 10.0,
                values = occupiedPorts
            }
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
        bool? Enabled = null);
}
