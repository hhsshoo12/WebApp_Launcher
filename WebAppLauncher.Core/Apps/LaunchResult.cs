namespace WebAppLauncher.Core;

public sealed record LaunchResult(
    InstalledApp App,
    Uri Uri,
    System.Diagnostics.Process? Process,
    string? LogPath,
    int? Port,
    Action? ReleasePortAction = null)
{
    private int portReleased;

    public bool HasBackend => Process is not null;

    public void ReleasePort()
    {
        if (Interlocked.Exchange(ref portReleased, 1) == 0)
        {
            ReleasePortAction?.Invoke();
        }
    }

    public void StopBackend()
    {
        if (Process is not { } process)
        {
            ReleasePort();
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
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            try
            {
                if (process.HasExited)
                {
                    ReleasePort();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
