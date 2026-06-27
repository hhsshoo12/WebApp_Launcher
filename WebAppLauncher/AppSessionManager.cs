using System.Windows;
using WebAppLauncher.Core;

namespace WebAppLauncher;

internal sealed class AppSessionManager
{
    private readonly List<ActiveAppSession> sessions = [];

    public ActiveAppSession Add(LaunchResult launch, AppWindow window)
    {
        var session = new ActiveAppSession(launch, window);
        sessions.Add(session);
        return session;
    }

    public void Remove(ActiveAppSession session)
    {
        sessions.Remove(session);
    }

    public ActiveAppSession? FindFirstApp(InstalledApp app)
    {
        return sessions.FirstOrDefault(session => IsSameApp(session.Launch.App, app));
    }

    public ActiveAppSession? FindReusableBackend(InstalledApp app)
    {
        return sessions.FirstOrDefault(session =>
            IsSameApp(session.Launch.App, app) &&
            (session.Launch.Process is null || !session.Launch.Process.HasExited));
    }

    public bool HasApp(InstalledApp app)
    {
        return sessions.Any(session => IsSameApp(session.Launch.App, app));
    }

    public bool HasPackage(string packageId)
    {
        return sessions.Any(session =>
            session.Launch.App.Manifest.Package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasVisibleWindowForLaunch(LaunchResult launch)
    {
        return sessions.Any(session =>
            ReferenceEquals(session.Launch, launch) &&
            session.Window.IsVisible);
    }

    public ActiveAppSession[] ForLaunch(LaunchResult launch)
    {
        return sessions
            .Where(session => ReferenceEquals(session.Launch, launch))
            .ToArray();
    }

    public void CloseAll()
    {
        foreach (var session in sessions.ToArray())
        {
            session.Window.Close();
        }
    }

    public void CloseWindowsAndStopBackend(int processId, string? packageId, string? version)
    {
        var session = sessions.FirstOrDefault(item =>
            item.Launch.Process?.Id == processId &&
            (string.IsNullOrWhiteSpace(packageId) ||
             item.Launch.App.Manifest.Package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(version) ||
             item.Launch.App.Manifest.Package.Version.Equals(version, StringComparison.OrdinalIgnoreCase)));
        if (session is null)
        {
            throw new InvalidOperationException("실행 중인 프로세스를 찾을 수 없습니다.");
        }

        foreach (var matchingSession in ForLaunch(session.Launch))
        {
            matchingSession.Window.Close();
        }

        session.Launch.StopBackend();
    }

    public ProcessManagerState GetProcessManagerState()
    {
        var occupiedPorts = PortManager.GetOccupiedPorts();
        var processes = sessions
            .Where(session => session.Launch.Process is { HasExited: false })
            .DistinctBy(session => session.Launch.Process!.Id)
            .Select(session => new ProcessManagerProcess(
                session.Launch.App.Manifest.Package.Id,
                session.Launch.App.Manifest.Package.Name,
                session.Launch.App.Manifest.Package.Version,
                DescribeRuntime(session.Launch.App.Manifest.Runtime),
                session.Launch.App.Manifest.Entry.Mode ?? "static",
                session.Launch.Port,
                session.Launch.Process!.Id,
                session.Launch.Process.ProcessName,
                session.Launch.LogPath))
            .ToArray();

        return new ProcessManagerState(
            new PortUsage(
                occupiedPorts.Count,
                PortManager.LastPort - PortManager.FirstPort + 1,
                occupiedPorts.Count / 10.0,
                occupiedPorts),
            processes);
    }

    public static void BringToFront(ActiveAppSession session)
    {
        var window = session.Window;
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState =
                session.Launch.App.Manifest.Window.Fullscreen ||
                session.Launch.App.Manifest.Window.StartMaximized
                    ? WindowState.Maximized
                    : WindowState.Normal;
        }

        var wasTopmost = window.Topmost;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = wasTopmost;
    }

    private static bool IsSameApp(InstalledApp left, InstalledApp right)
    {
        return left.Manifest.Package.Id.Equals(right.Manifest.Package.Id, StringComparison.OrdinalIgnoreCase) &&
               left.Manifest.Package.Version.Equals(right.Manifest.Package.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeRuntime(RuntimeInfo runtime)
    {
        var parts = new List<string>();
        if (!runtime.Python.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(runtime.Python);
        }

        if (!runtime.Node.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(runtime.Node);
        }

        return parts.Count == 0 ? "static" : string.Join(", ", parts);
    }
}

internal sealed record ActiveAppSession(LaunchResult Launch, AppWindow Window);

internal sealed record ProcessManagerState(PortUsage Ports, IReadOnlyList<ProcessManagerProcess> Processes);

internal sealed record PortUsage(
    int Occupied,
    int Total,
    double Percent,
    IReadOnlyList<int> Values);

internal sealed record ProcessManagerProcess(
    string PackageId,
    string Name,
    string Version,
    string Runtime,
    string Mode,
    int? Port,
    int ProcessId,
    string ProcessName,
    string? LogPath);
