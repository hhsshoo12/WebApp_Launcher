using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WebAppLauncher.Core;

namespace WebAppLauncher;

public partial class AppWindow : Window
{
    private readonly LaunchResult launch;
    private readonly string sessionProfileDirectory;
    private readonly bool developerMode;

    public AppWindow(LaunchResult launch, bool developerMode)
    {
        this.launch = launch;
        this.developerMode = developerMode;
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
        Loaded += LoadedAsync;
        Closed += WindowClosedAsync;
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

            Browser.Source = launch.Uri;
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

    private async void WindowClosedAsync(object? sender, EventArgs e)
    {
        StopBackendAndReleasePort();

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
    }

    private void StopBackendAndReleasePort()
    {
        if (launch.Process is not { } process)
        {
            launch.ReleasePort();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            if (process.HasExited)
            {
                launch.ReleasePort();
            }
        }
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
}
