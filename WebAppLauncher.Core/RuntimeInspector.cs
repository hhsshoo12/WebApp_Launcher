using System.Text.RegularExpressions;

namespace WebAppLauncher.Core;

public sealed record RuntimeStatus(
    string Id,
    string Name,
    string ExpectedVersion,
    string? InstalledVersion,
    string Status,
    string Path);

public sealed class RuntimeInspector
{
    private readonly WebAppPaths paths;

    public RuntimeInspector(WebAppPaths paths)
    {
        this.paths = paths;
    }

    public async Task<IReadOnlyList<RuntimeStatus>> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var specs = new[]
        {
            new RuntimeSpec("python313", "Python 3.13", "3.13.14", paths.GetPythonExecutable("python313"), "--version"),
            new RuntimeSpec("python314", "Python 3.14", "3.14.6", paths.GetPythonExecutable("python314"), "--version"),
            new RuntimeSpec("nodejs-lts-22", "Node.js LTS 22", "22.23.0", paths.GetNodeExecutable("nodejs-lts-22"), "--version"),
            new RuntimeSpec("nodejs-lts-24", "Node.js LTS 24", "24.17.0", paths.GetNodeExecutable("nodejs-lts-24"), "--version"),
            new RuntimeSpec("git", "Git", "2.54.0", paths.GitExecutable, "--version"),
            new RuntimeSpec("uv", "uv", "0.11.23", paths.UvExecutable, "--version"),
            new RuntimeSpec("pnpm", "pnpm", "11.8.0", ResolvePnpmExecutable(), "--version")
        };

        return await Task.WhenAll(specs.Select(spec => InspectAsync(spec, cancellationToken)));
    }

    private async Task<RuntimeStatus> InspectAsync(
        RuntimeSpec spec,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(spec.Path))
        {
            return new RuntimeStatus(
                spec.Id,
                spec.Name,
                spec.ExpectedVersion,
                null,
                "missing",
                spec.Path);
        }

        try
        {
            var executable = spec.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
                : spec.Path;
            var arguments = spec.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? $"/d /c \"\"{spec.Path}\" {spec.Arguments}\""
                : spec.Arguments;
            var result = await CommandRunner.RunAsync(
                executable,
                arguments,
                Path.GetDirectoryName(spec.Path)!,
                cancellationToken: cancellationToken);
            var output = $"{result.StandardOutput} {result.StandardError}".Trim();
            var installed = ExtractVersion(output);
            var status = result.ExitCode != 0 || installed is null
                ? "error"
                : CompareVersions(installed, spec.ExpectedVersion);
            return new RuntimeStatus(
                spec.Id,
                spec.Name,
                spec.ExpectedVersion,
                installed ?? output,
                status,
                spec.Path);
        }
        catch
        {
            return new RuntimeStatus(
                spec.Id,
                spec.Name,
                spec.ExpectedVersion,
                null,
                "error",
                spec.Path);
        }
    }

    private string ResolvePnpmExecutable()
    {
        var exe = Path.Combine(paths.Tools, "pnpm", "pnpm.exe");
        return File.Exists(exe) ? exe : paths.PnpmExecutable;
    }

    private static string? ExtractVersion(string output)
    {
        var match = Regex.Match(output, @"(?<!\d)(\d+\.\d+(?:\.\d+)?)(?!\d)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string CompareVersions(string installed, string expected)
    {
        if (!Version.TryParse(installed, out var installedVersion) ||
            !Version.TryParse(expected, out var expectedVersion))
        {
            return installed.Equals(expected, StringComparison.OrdinalIgnoreCase)
                ? "current"
                : "update";
        }

        var comparison = installedVersion.CompareTo(expectedVersion);
        return comparison == 0 ? "current" : comparison > 0 ? "newer" : "update";
    }

    private sealed record RuntimeSpec(
        string Id,
        string Name,
        string ExpectedVersion,
        string Path,
        string Arguments);
}
