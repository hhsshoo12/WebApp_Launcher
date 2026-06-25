using System.Diagnostics;
using System.Text;

namespace WebAppLauncher.Core;

public sealed class AppLauncher
{
    private readonly ToolResolver tools;
    private readonly PortManager ports = new();

    public AppLauncher(WebAppPaths paths)
    {
        tools = new ToolResolver(paths);
    }

    public LaunchResult Launch(InstalledApp app)
    {
        Directory.CreateDirectory(app.DataDirectory);
        Directory.CreateDirectory(app.LogDirectory);
        Directory.CreateDirectory(app.TempDirectory);

        if (app.Manifest.Entry.Mode == "static")
        {
            var htmlPath = Path.Combine(app.SourceDirectory, app.Manifest.Entry.Html);
            return new LaunchResult(app, new Uri(htmlPath), null, null, null);
        }

        if (string.IsNullOrWhiteSpace(app.Manifest.Entry.Server))
        {
            throw new InvalidOperationException("Server mode requires entry.server.");
        }

        var serverPath = Path.Combine(app.SourceDirectory, app.Manifest.Entry.Server);
        if (!File.Exists(serverPath))
        {
            throw new FileNotFoundException("Backend entry file was not found.", serverPath);
        }

        var logPath = Path.Combine(app.LogDirectory, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        var port = ports.AllocatePort();
        try
        {
            var process = StartBackend(app, serverPath, logPath, port);
            var result = new LaunchResult(
                app,
                new Uri($"http://{app.Manifest.Network.Host}:{port}"),
                process,
                logPath,
                port,
                () => ports.ReleasePort(port));
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => result.ReleasePort();
            return result;
        }
        catch
        {
            ports.ReleasePort(port);
            throw;
        }
    }

    private Process StartBackend(InstalledApp app, string serverPath, string logPath, int port)
    {
        var extension = Path.GetExtension(serverPath);
        var executable = extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
            ? tools.ResolvePython(app.Manifest.Runtime.Python)
            : extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
                ? tools.ResolveNode(app.Manifest.Runtime.Node)
                : throw new InvalidOperationException("Backend entry must be app.py or app.js.");

        var startInfo = new ProcessStartInfo(executable, Quote(serverPath))
        {
            WorkingDirectory = app.SourceDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.Environment["WEBAPP_HOST"] = app.Manifest.Network.Host;
        startInfo.Environment["WEBAPP_PORT"] = port.ToString();
        startInfo.Environment["WEBAPP_ROOT"] = app.InstallDirectory;
        startInfo.Environment["WEBAPP_SOURCE_DIR"] = app.SourceDirectory;
        startInfo.Environment["WEBAPP_DATA_DIR"] = app.DataDirectory;
        startInfo.Environment["WEBAPP_TEMP_DIR"] = app.TempDirectory;
        startInfo.Environment["WEBAPP_LOG_DIR"] = app.LogDirectory;

        if (Directory.Exists(app.VenvDirectory))
        {
            startInfo.Environment["VIRTUAL_ENV"] = app.VenvDirectory;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start backend process.");
        _ = PumpLogsAsync(process, logPath);
        return process;
    }

    private static async Task PumpLogsAsync(Process process, string logPath)
    {
        await using var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        var stdout = CopyAsync(process.StandardOutput, writer, "OUT");
        var stderr = CopyAsync(process.StandardError, writer, "ERR");
        await Task.WhenAll(stdout, stderr);
    }

    private static async Task CopyAsync(StreamReader reader, StreamWriter writer, string prefix)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync($"[{prefix}] {line}");
            await writer.FlushAsync();
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
