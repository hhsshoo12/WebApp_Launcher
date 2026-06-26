using System.IO;
using System.Windows;
using WebAppLauncher.Core;

namespace WebAppLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryGetWapkPath(e.Args, out var wapkPath))
        {
            var dialog = new InstallPromptWindow(wapkPath);
            dialog.ShowDialog();
            Shutdown();
            return;
        }

        if (TryGetWebAppPath(e.Args, out var webAppPath))
        {
            LaunchFromWebAppAsync(webAppPath).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        var main = new MainWindow();
        main.Show();
    }

    private async Task LaunchFromWebAppAsync(string webAppPath)
    {
        try
        {
            var paths = new WebAppPaths();
            paths.EnsureRootLayout();
            var repository = new AppRepository(paths);
            var app = repository.FindByManifestPath(webAppPath)
                ?? throw new FileNotFoundException(
                    $"설치된 앱을 찾을 수 없습니다: {webAppPath}",
                    webAppPath);

            var launcher = new AppLauncher(paths);
            var result = launcher.Launch(app);
            var window = new AppWindow(result, developerMode: false, ownsBackend: true);
            window.ShowDialog();

            if (result.HasBackend)
            {
                result.StopBackend();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "앱 실행 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool TryGetWapkPath(string[] args, out string path)
    {
        path = string.Empty;
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            return false;
        }

        var ext = Path.GetExtension(args[0]);
        if (!ext.Equals(".wapk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = Path.GetFullPath(args[0]);
        return true;
    }

    private static bool TryGetWebAppPath(string[] args, out string path)
    {
        path = string.Empty;
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            return false;
        }

        var ext = Path.GetExtension(args[0]);
        if (!ext.Equals(".webapp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = Path.GetFullPath(args[0]);
        return true;
    }
}
