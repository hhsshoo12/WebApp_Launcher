namespace WebAppLauncher.Core;

public sealed class GitSourceResolver
{
    private static readonly SemaphoreSlim GitLock = new(1, 1);
    private readonly WebAppPaths paths;
    private readonly ToolResolver tools;
    private readonly Func<SourceInfo, string> remoteUrl;

    public GitSourceResolver(WebAppPaths paths, Func<SourceInfo, string>? remoteUrl = null)
    {
        this.paths = paths;
        tools = new ToolResolver(paths);
        this.remoteUrl = remoteUrl ??
            (source => $"https://github.com/{source.Owner}/{source.Repo}.git");
    }

    public async Task<ResolvedGitSource> ResolveAsync(
        SourceInfo source,
        bool createSnapshot = true,
        CancellationToken cancellationToken = default)
    {
        if (!source.Provider.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only GitHub sources are supported.");
        }

        await GitLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.GitCache);
            var cacheDirectory = Path.Combine(paths.GitCache, $"{source.Owner}@{source.Repo}");
            var url = remoteUrl(source);
            if (!Directory.Exists(Path.Combine(cacheDirectory, ".git")))
            {
                if (Directory.Exists(cacheDirectory))
                {
                    Directory.Delete(cacheDirectory, recursive: true);
                }

                await RunCheckedAsync(
                    $"clone --no-checkout {Quote(url)} {Quote(cacheDirectory)}",
                    paths.GitCache,
                    cancellationToken);
            }

            await RunCheckedAsync("remote set-url origin " + Quote(url), cacheDirectory, cancellationToken);
            var branch = source.Branch == "*"
                ? await ResolveDefaultBranchAsync(url, cacheDirectory, cancellationToken)
                : source.Branch;

            await RunCheckedAsync(
                $"fetch --prune origin {Quote(branch)}",
                cacheDirectory,
                cancellationToken);

            var requestedCommit = source.Commit == "*" ? "FETCH_HEAD" : source.Commit;
            var commit = await ReadCheckedAsync(
                $"rev-parse {Quote(requestedCommit + "^{commit}")}",
                cacheDirectory,
                cancellationToken);
            if (commit.Length != 40 || commit.Any(ch => !Uri.IsHexDigit(ch)))
            {
                throw new InvalidOperationException($"Git returned an invalid commit: {commit}");
            }

            commit = commit.ToLowerInvariant();
            if (!createSnapshot)
            {
                return new ResolvedGitSource(source, branch, commit, cacheDirectory);
            }

            var snapshot = Path.Combine(
                paths.GitCache,
                ".resolved",
                WebAppPaths.SanitizeSegment($"{source.Owner}@{source.Repo}"),
                commit);
            if (!Directory.Exists(snapshot))
            {
                await RunCheckedAsync($"checkout --force {Quote(commit)}", cacheDirectory, cancellationToken);
                var temporary = snapshot + $".tmp-{Guid.NewGuid():N}";
                try
                {
                    CopySnapshot(cacheDirectory, temporary);
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                    Directory.Move(temporary, snapshot);
                }
                finally
                {
                    if (Directory.Exists(temporary))
                    {
                        Directory.Delete(temporary, recursive: true);
                    }
                }
            }

            return new ResolvedGitSource(source, branch, commit, snapshot);
        }
        finally
        {
            GitLock.Release();
        }
    }

    private async Task<string> ResolveDefaultBranchAsync(
        string url,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var output = await ReadCheckedAsync(
            $"ls-remote --symref {Quote(url)} HEAD",
            workingDirectory,
            cancellationToken);
        var prefix = "ref: refs/heads/";
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidOperationException("GitHub default branch could not be resolved.");
        }

        var tab = line.IndexOf('\t');
        return tab > prefix.Length ? line[prefix.Length..tab] : line[prefix.Length..].Trim();
    }

    private async Task RunCheckedAsync(
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await CommandRunner.RunAsync(
            tools.Git,
            arguments,
            workingDirectory,
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                result.StandardError.Trim().Length > 0
                    ? result.StandardError.Trim()
                    : result.StandardOutput.Trim());
        }
    }

    private async Task<string> ReadCheckedAsync(
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await CommandRunner.RunAsync(
            tools.Git,
            arguments,
            workingDirectory,
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }

        return result.StandardOutput.Trim();
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void CopySnapshot(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopySnapshot(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
