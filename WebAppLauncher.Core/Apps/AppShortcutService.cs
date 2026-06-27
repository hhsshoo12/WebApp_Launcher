using System.IO;
using System.Runtime.Versioning;

namespace WebAppLauncher.Core;

[SupportedOSPlatform("windows")]
public sealed class AppShortcutService
{
    private readonly Func<string> desktopDirectory;
    private readonly IShortcutWriter writer;

    public AppShortcutService()
        : this(GetDesktopDirectory, new ShellLinkShortcutWriter())
    {
    }

    public AppShortcutService(IShortcutWriter writer)
        : this(GetDesktopDirectory, writer)
    {
    }

    public AppShortcutService(Func<string> desktopDirectory, IShortcutWriter writer)
    {
        this.desktopDirectory = desktopDirectory;
        this.writer = writer;
    }

    public string CreateDesktopShortcut(InstalledApp app, string launcherExePath)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        if (string.IsNullOrWhiteSpace(launcherExePath))
        {
            throw new ArgumentException("Launcher executable path is required.", nameof(launcherExePath));
        }

        var desktop = desktopDirectory();
        if (string.IsNullOrWhiteSpace(desktop))
        {
            throw new InvalidOperationException("데스크톱 폴더 위치를 확인할 수 없습니다.");
        }

        Directory.CreateDirectory(desktop);
        var shortcutPath = Path.Combine(desktop, SanitizeFileName(app.Manifest.Package.Name) + ".lnk");
        var iconPath = ResolveIconPath(app);

        writer.Write(
            shortcutPath: shortcutPath,
            targetPath: launcherExePath,
            arguments: QuoteArgument(app.ManifestPath),
            workingDirectory: Path.GetDirectoryName(launcherExePath) ?? string.Empty,
            iconLocation: iconPath,
            description: $"{app.Manifest.Package.Name} {app.Manifest.Package.Version}");
        return shortcutPath;
    }

    private static string ResolveIconPath(InstalledApp app)
    {
        var icon = app.Manifest.Entry.Icon;
        if (string.IsNullOrWhiteSpace(icon))
        {
            return string.Empty;
        }

        var resolved = Path.Combine(app.SourceDirectory, icon.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(resolved) ||
            !Path.GetExtension(resolved).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"{resolved},0";
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return $"\"{value}\"";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            buffer[length++] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
        }
        return new string(buffer, 0, length).Trim();
    }

    private static string GetDesktopDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
}
