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
        var versions = ReadManagedVersions();
        var specs = new[]
        {
            new RuntimeSpec("python313", "Python 3.13", VersionOf("python313"), paths.GetPythonExecutable("python313"), "--version"),
            new RuntimeSpec("python314", "Python 3.14", VersionOf("python314"), paths.GetPythonExecutable("python314"), "--version"),
            new RuntimeSpec("nodejs-lts-22", "Node.js LTS 22", VersionOf("nodejs-lts-22"), paths.GetNodeExecutable("nodejs-lts-22"), "--version"),
            new RuntimeSpec("nodejs-lts-24", "Node.js LTS 24", VersionOf("nodejs-lts-24"), paths.GetNodeExecutable("nodejs-lts-24"), "--version"),
            new RuntimeSpec("git", "Git", VersionOf("git"), paths.GitExecutable, "--version"),
            new RuntimeSpec("uv", "uv", VersionOf("uv"), paths.UvExecutable, "--version"),
            new RuntimeSpec("pnpm", "pnpm", VersionOf("pnpm"), ResolvePnpmExecutable(), "--version")
        };

        return await Task.WhenAll(specs.Select(spec => InspectAsync(spec, cancellationToken)));

        string VersionOf(string id) => versions.TryGetValue(id, out var value) ? value : "unknown";
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
                : spec.ExpectedVersion == "unknown"
                    ? "unmanaged"
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

    private IReadOnlyDictionary<string, string> ReadManagedVersions()
    {
        var path = Path.Combine(paths.Root, "runtime-manifest.toml");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var document = SimpleToml.ParseFile(path);
            var ids = new[]
            {
                "python313", "python314", "nodejs-lts-22", "nodejs-lts-24", "git", "uv", "pnpm"
            };
            return ids
                .Select(id => (Id: id, Version: document.GetOptionalString("versions", id)))
                .Where(item => item.Version is not null)
                .ToDictionary(item => item.Id, item => item.Version!, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
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
