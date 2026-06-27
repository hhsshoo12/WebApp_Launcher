using System.Reflection;

namespace WebAppLauncher.Core;

public static class LauncherVersion
{
    public static string Current { get; } =
        typeof(LauncherVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(LauncherVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
