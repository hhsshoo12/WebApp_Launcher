using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using WebAppLauncher.Core;

namespace WebAppLauncher;

public partial class AppWindow : Window
{
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LaunchResult launch;
    private readonly string sessionProfileDirectory;
    private readonly bool developerMode;
    private readonly bool ownsBackend;
    private bool fullscreen;
    private WindowState stateBeforeFullscreen;
    private ResizeMode resizeModeBeforeFullscreen;
    private WindowStyle styleBeforeFullscreen;

    public event EventHandler? CleanupCompleted;

    public AppWindow(LaunchResult launch, bool developerMode, bool ownsBackend)
    {
        this.launch = launch;
        this.developerMode = developerMode;
        this.ownsBackend = ownsBackend;
        sessionProfileDirectory = Path.Combine(
            Path.GetTempPath(),
            "WebAppLauncher",
            "webview",
            Guid.NewGuid().ToString("N"));
        InitializeComponent();

        Title = $"{launch.App.Manifest.Package.Name} {launch.App.Manifest.Package.Version}";
        Width = launch.App.Manifest.Window.Width;
        Height = launch.App.Manifest.Window.Height;
        ResizeMode = launch.App.Manifest.Window.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        Topmost = launch.App.Manifest.Window.AlwaysOnTop;
        ConfigureWindowStyle();
        Loaded += LoadedAsync;
        Closed += WindowClosedAsync;
        StateChanged += (_, _) => SendWindowState();
    }

    private void ConfigureWindowStyle()
    {
        var window = launch.App.Manifest.Window;
        if (window.Transparent)
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }

        if (window.Transparent || window.Borderless || window.Fullscreen)
        {
            WindowStyle = WindowStyle.None;
        }

        if (window.Fullscreen)
        {
            stateBeforeFullscreen = WindowState.Normal;
            resizeModeBeforeFullscreen = window.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            styleBeforeFullscreen = window.Transparent || window.Borderless
                ? WindowStyle.None
                : WindowStyle.SingleBorderWindow;
            fullscreen = true;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else if (window.StartMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await DeleteDirectoryWithRetriesAsync(
                Path.Combine(launch.App.InstallDirectory, "webview-profile"),
                attempts: 2);
            Directory.CreateDirectory(sessionProfileDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: sessionProfileDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled =
                developerMode || launch.App.Manifest.Window.Devtools;
            Browser.CoreWebView2.WebMessageReceived += AppWebMessageReceived;
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WindowApiScript);
            if (launch.App.Manifest.Window.Transparent)
            {
                Browser.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            }

            Browser.Source = launch.Uri;
            SendWindowState();
            if (developerMode)
            {
                Browser.CoreWebView2.OpenDevToolsWindow();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "앱 실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void AppWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedSource(e.Source))
        {
            return;
        }

        WindowApiCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<WindowApiCommand>(e.WebMessageAsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (command is null || command.Type != "webapp-window" || string.IsNullOrWhiteSpace(command.Id))
        {
            return;
        }

        try
        {
            switch (command.Action)
            {
                case "minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "maximize":
                    ExitFullscreen();
                    WindowState = WindowState.Maximized;
                    break;
                case "restore":
                    ExitFullscreen();
                    WindowState = WindowState.Normal;
                    break;
                case "toggleMaximize":
                    ExitFullscreen();
                    WindowState = WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    break;
                case "setFullscreen":
                    SetFullscreen(command.Value == true);
                    break;
                case "toggleFullscreen":
                    SetFullscreen(!fullscreen);
                    break;
                case "setAlwaysOnTop":
                    Topmost = command.Value == true;
                    break;
                case "startDrag":
                    StartNativeDrag();
                    break;
                case "getState":
                    SendWindowResponse(command.Id, WindowStatePayload());
                    return;
                case "close":
                    SendWindowResponse(command.Id, WindowStatePayload());
                    Dispatcher.BeginInvoke(Close);
                    return;
                default:
                    throw new InvalidOperationException($"Unknown window action: {command.Action}");
            }

            SendWindowResponse(command.Id, WindowStatePayload());
            SendWindowState();
        }
        catch (Exception ex)
        {
            Send(new { type = "webapp-window-response", id = command.Id, ok = false, error = ex.Message });
        }
    }

    private bool IsTrustedSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (launch.Uri.IsFile)
        {
            var sourceRoot = Path.GetFullPath(launch.App.SourceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var localPath = Path.GetFullPath(uri.LocalPath);
            return uri.IsFile && localPath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase);
        }

        return uri.Scheme.Equals(launch.Uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals(launch.Uri.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == launch.Uri.Port;
    }

    private void SetFullscreen(bool enabled)
    {
        if (enabled == fullscreen)
        {
            return;
        }

        if (enabled)
        {
            stateBeforeFullscreen = WindowState;
            resizeModeBeforeFullscreen = ResizeMode;
            styleBeforeFullscreen = WindowStyle;
            fullscreen = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            return;
        }

        ExitFullscreen();
    }

    private void ExitFullscreen()
    {
        if (!fullscreen)
        {
            return;
        }

        fullscreen = false;
        WindowStyle = styleBeforeFullscreen;
        ResizeMode = resizeModeBeforeFullscreen;
        WindowState = stateBeforeFullscreen == WindowState.Minimized
            ? WindowState.Normal
            : stateBeforeFullscreen;
    }

    private void StartNativeDrag()
    {
        if (fullscreen || WindowState == WindowState.Maximized)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, WmNcLButtonDown, HtCaption, 0);
    }

    private object WindowStatePayload()
    {
        return new
        {
            minimized = WindowState == WindowState.Minimized,
            maximized = WindowState == WindowState.Maximized,
            fullscreen,
            alwaysOnTop = Topmost,
            borderless = WindowStyle == WindowStyle.None
        };
    }

    private void SendWindowResponse(string id, object result)
    {
        Send(new { type = "webapp-window-response", id, ok = true, result });
    }

    private void SendWindowState()
    {
        if (Browser.CoreWebView2 is not null)
        {
            Send(new { type = "webapp-window-state", state = WindowStatePayload() });
        }
    }

    private void Send(object value)
    {
        Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(value, JsonOptions));
    }

    private async void WindowClosedAsync(object? sender, EventArgs e)
    {
        if (ownsBackend)
        {
            launch.StopBackend();
        }

        try
        {
            if (Browser.CoreWebView2 is not null)
            {
                await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.AllProfile);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            Browser.Dispose();
        }

        await DeleteDirectoryWithRetriesAsync(sessionProfileDirectory, attempts: 8);
        CleanupCompleted?.Invoke(this, EventArgs.Empty);
    }

    private static async Task DeleteDirectoryWithRetriesAsync(string path, int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                await Task.Delay(150 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < attempts - 1)
            {
                await Task.Delay(150 * (attempt + 1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, int wParam, int lParam);

    private sealed record WindowApiCommand(
        string Type,
        string Id,
        string Action,
        bool? Value = null);

    private const string WindowApiScript = """
        (() => {
          if (window.top !== window || window.webapp?.window || !window.chrome?.webview) return;
          const pending = new Map();
          let sequence = 0;
          const call = (action, value) => new Promise((resolve, reject) => {
            const id = `${Date.now().toString(36)}-${(++sequence).toString(36)}`;
            pending.set(id, { resolve, reject });
            window.chrome.webview.postMessage({ type: "webapp-window", id, action, value });
          });
          window.chrome.webview.addEventListener("message", ({ data }) => {
            if (data?.type === "webapp-window-response") {
              const request = pending.get(data.id);
              if (!request) return;
              pending.delete(data.id);
              if (data.ok) request.resolve(data.result);
              else request.reject(new Error(data.error || "Window command failed."));
            }
            if (data?.type === "webapp-window-state") {
              window.dispatchEvent(new CustomEvent("webappwindowstatechange", {
                detail: data.state
              }));
            }
          });
          window.webapp = Object.freeze({
            ...(window.webapp || {}),
            window: Object.freeze({
              minimize: () => call("minimize"),
              maximize: () => call("maximize"),
              restore: () => call("restore"),
              toggleMaximize: () => call("toggleMaximize"),
              setFullscreen: value => call("setFullscreen", Boolean(value)),
              toggleFullscreen: () => call("toggleFullscreen"),
              setAlwaysOnTop: value => call("setAlwaysOnTop", Boolean(value)),
              startDrag: () => call("startDrag"),
              close: () => call("close"),
              getState: () => call("getState")
            })
          });
        })();
        """;
}
