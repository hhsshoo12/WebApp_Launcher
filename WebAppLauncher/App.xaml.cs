using System.IO;
using System.Windows;

namespace WebAppLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (TryGetPackagePath(e.Args, out var path))
        {
            var dialog = new InstallPromptWindow(path);
            dialog.ShowDialog();
            Shutdown();
            return;
        }
        var main = new MainWindow();
        main.Show();
    }

    private static bool TryGetPackagePath(string[] args, out string path)
    {
        path = string.Empty;
        if (args.Length == 0)
        {
            return false;
        }
        var candidate = args[0];
        if (!File.Exists(candidate))
        {
            return false;
        }
        var ext = Path.GetExtension(candidate);
        if (!ext.Equals(".wapk", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".webapp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        path = Path.GetFullPath(candidate);
        return true;
    }
}
