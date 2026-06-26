using System.IO;
using System.Windows;
using System.Windows.Media;
using WebAppLauncher.Core;

namespace WebAppLauncher;

public partial class InstallPromptWindow : Window
{
    private readonly string packagePath;
    private readonly WebAppPaths paths = new();
    private readonly AppInstaller installer;
    private readonly AppShortcutService shortcutService;
    private bool installInProgress;
    private bool installSucceeded;
    private InstalledApp? installedApp;

    public InstallPromptWindow(string packagePath, AppShortcutService? shortcutService = null)
    {
        this.packagePath = packagePath;
        installer = new AppInstaller(paths);
        this.shortcutService = shortcutService ?? new AppShortcutService();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var manifest = LoadManifest(packagePath);
            AppNameText.Text = manifest.Package.Name;
            var commitDisplay = manifest.Source.Commit == "*"
                ? "(최신 커밋)"
                : ShortCommit(manifest.Source.Commit);
            MetaText.Text = $"개발자 {manifest.Source.Owner}    ·    커밋 {commitDisplay}";
        }
        catch (Exception ex)
        {
            AppNameText.Text = Path.GetFileName(packagePath);
            AppNameSuffixText.Text = "을(를) 설치할 수 없습니다.";
            MetaText.Text = "지원하지 않는 패키지 파일입니다.";
            MetaText.Foreground = Brushes.IndianRed;
            InstallButton.IsEnabled = false;
            ShowStatus(ex.Message);
        }
    }

    private static WapkManifest LoadManifest(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".webapp", StringComparison.OrdinalIgnoreCase))
        {
            var installed = TomlManifestStore.LoadWebApp(path);
            return new WapkManifest(
                Format: 1,
                Package: installed.Package,
                Source: installed.Source ?? new SourceInfo(
                    "github", ExtractOwnerFromId(installed.Package.Id), "", "*", "*", ""),
                Runtime: installed.Runtime,
                Entry: installed.Entry,
                Window: installed.Window);
        }
        return TomlManifestStore.LoadWapk(path);
    }

    private static string ExtractOwnerFromId(string id)
    {
        var at = id.IndexOf('@');
        return at >= 0 ? id[..at] : id;
    }

    private static string ShortCommit(string commit)
    {
        return commit.Length <= 8 ? commit : commit[..8];
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (installInProgress)
        {
            return;
        }
        installInProgress = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ShowStatus("앱 소스와 의존성을 설치하는 중입니다.");
        try
        {
            installedApp = await installer.InstallAsync(packagePath);
            installSucceeded = true;
            ShowStatus($"{installedApp.Manifest.Package.Name} {installedApp.Manifest.Package.Version}을(를) 설치했습니다.");
            InstallButton.Content = "바탕화면 바로가기 만들기";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= InstallButton_Click;
            InstallButton.Click += ShortcutButton_Click;
        }
        catch (Exception ex)
        {
            ShowStatus($"설치에 실패했습니다: {ex.Message}");
        }
        finally
        {
            if (!installSucceeded)
            {
                CancelButton.Content = "닫기";
                CancelButton.IsEnabled = true;
            }
            installInProgress = false;
        }
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (installedApp is null)
        {
            return;
        }

        try
        {
            var launcherPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("런처 경로를 확인할 수 없습니다.");
            var shortcutPath = shortcutService.CreateDesktopShortcut(installedApp, launcherPath);
            ShowStatus($"바탕화면 바로가기를 만들었습니다: {shortcutPath}");
            InstallButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            ShowStatus($"바로가기 만들기 실패: {ex.Message}");
            InstallButton.IsEnabled = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
