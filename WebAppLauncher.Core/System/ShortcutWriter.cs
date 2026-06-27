using System.IO;
using System.Runtime.Versioning;

namespace WebAppLauncher.Core;

public interface IShortcutWriter
{
    void Write(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string iconLocation,
        string description);
}

[SupportedOSPlatform("windows")]
public sealed class ShellLinkShortcutWriter : IShortcutWriter
{
    public void Write(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string iconLocation,
        string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available on this system.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = targetPath;
                shortcut.Arguments = arguments ?? string.Empty;
                shortcut.WorkingDirectory = workingDirectory ?? string.Empty;
                shortcut.WindowStyle = 1;
                if (!string.IsNullOrEmpty(iconLocation))
                {
                    shortcut.IconLocation = iconLocation;
                }
                if (!string.IsNullOrEmpty(description))
                {
                    shortcut.Description = description;
                }
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}
