using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WebAppLauncher.Core;

namespace WebAppLauncher;

public partial class AppWindow : Window
{
    private readonly LaunchResult launch;

    public AppWindow(LaunchResult launch)
    {
        this.launch = launch;
        InitializeComponent();

        Title = $"{launch.App.Manifest.Package.Name} {launch.App.Manifest.Package.Version}";
        Width = launch.App.Manifest.Window.Width;
        Height = launch.App.Manifest.Window.Height;
        ResizeMode = launch.App.Manifest.Window.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        Loaded += LoadedAsync;
        Closed += WindowClosed;
    }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        var profileDirectory = Path.Combine(launch.App.InstallDirectory, "webview-profile");
        Directory.CreateDirectory(profileDirectory);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory);
        await Browser.EnsureCoreWebView2Async(environment);
        if (!launch.App.Manifest.Window.Devtools)
        {
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        }

        Browser.Source = launch.Uri;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        if (launch.Process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
