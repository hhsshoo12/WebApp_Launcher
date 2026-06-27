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

    private RuntimeSpec CreateSpec(string id, string expectedVersion)
    {
        if (id.StartsWith("python", StringComparison.OrdinalIgnoreCase))
        {
            var displayVersion = id.Substring("python".Length);
            if (displayVersion.Length >= 2)
            {
                displayVersion = displayVersion.Insert(1, ".");
            }
            return new RuntimeSpec(id, $"Python {displayVersion}", expectedVersion, paths.GetPythonExecutable(id), ["--version"]);
        }
        if (id.StartsWith("nodejs-lts-", StringComparison.OrdinalIgnoreCase))
        {
            var displayVersion = id.Substring("nodejs-lts-".Length);
            return new RuntimeSpec(id, $"Node.js LTS {displayVersion}", expectedVersion, paths.GetNodeExecutable(id), ["--version"]);
        }
        if (id.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeSpec(id, "Git", expectedVersion, paths.GitExecutable, ["--version"]);
        }
        if (id.Equals("uv", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeSpec(id, "uv", expectedVersion, paths.UvExecutable, ["--version"]);
        }
        if (id.Equals("pnpm", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeSpec(id, "pnpm", expectedVersion, ResolvePnpmExecutable(), ["--version"]);
        }
        return new RuntimeSpec(id, id, expectedVersion, Path.Combine(paths.Tools, id), ["--version"]);
    }

    public async Task<IReadOnlyList<RuntimeStatus>> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var versions = ReadManagedVersions();
        var ids = versions.Keys.ToList();
        if (ids.Count == 0)
        {
            ids = ["python313", "python314", "nodejs-lts-22", "nodejs-lts-24", "git", "uv", "pnpm"];
        }
        else
        {
            if (!ids.Contains("git", StringComparer.OrdinalIgnoreCase)) ids.Add("git");
            if (!ids.Contains("uv", StringComparer.OrdinalIgnoreCase)) ids.Add("uv");
            if (!ids.Contains("pnpm", StringComparer.OrdinalIgnoreCase)) ids.Add("pnpm");
        }

        var specs = ids.Select(id => CreateSpec(id, VersionOf(id))).ToArray();
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
            var result = await CommandRunner.RunAsync(
                spec.Path,
                spec.Arguments,
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
            var ids = document.GetSectionKeys("versions");
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
        IReadOnlyList<string> Arguments);
}
