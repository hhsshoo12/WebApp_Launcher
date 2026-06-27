namespace WebAppLauncher.Core;

public sealed class GitSourceResolver
{
    private static readonly SemaphoreSlim GitLock = new(1, 1);
    private readonly WebAppPaths paths;
    private readonly ToolResolver tools;
    private readonly Func<SourceInfo, string> remoteUrl;

    public GitSourceResolver(
        WebAppPaths paths,
        Func<SourceInfo, string>? remoteUrl = null,
        bool requirePublicRepository = true)
    {
        this.paths = paths;
        tools = new ToolResolver(paths);
        this.remoteUrl = remoteUrl ??
            (source => $"https://github.com/{source.Owner}/{source.Repo}.git");
        RequirePublicRepository = requirePublicRepository;
    }

    public bool RequirePublicRepository { get; }

    public async Task<ResolvedGitSource> ResolveAsync(
        SourceInfo source,
        bool createSnapshot = true,
        CancellationToken cancellationToken = default)
    {
        if (!source.Provider.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only GitHub sources are supported.");
        }

        if (RequirePublicRepository)
        {
            await EnsurePublicRepositoryAsync(source, cancellationToken);
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
                    ["clone", "--no-checkout", url, cacheDirectory],
                    paths.GitCache,
                    cancellationToken);
            }

            await RunCheckedAsync(["remote", "set-url", "origin", url], cacheDirectory, cancellationToken);
            var branch = source.Branch == "*"
                ? await ResolveDefaultBranchAsync(url, cacheDirectory, cancellationToken)
                : source.Branch;

            await RunCheckedAsync(
                ["fetch", "--prune", "origin", branch],
                cacheDirectory,
                cancellationToken);

            var requestedCommit = source.Commit == "*" ? "FETCH_HEAD" : source.Commit;
            var commit = await ReadCheckedAsync(
                ["rev-parse", requestedCommit + "^{commit}"],
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
                await RunCheckedAsync(["checkout", "--force", commit], cacheDirectory, cancellationToken);
                var temporary = snapshot + $".tmp-{Guid.NewGuid():N}";
                try
                {
                    AppInstaller.CopyDirectory(cacheDirectory, temporary);
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

    private static async Task EnsurePublicRepositoryAsync(
        SourceInfo source,
        CancellationToken cancellationToken)
    {
        var apiUrl = $"https://api.github.com/repos/{source.Owner}/{source.Repo}";
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "WebAppLauncher");
            using var response = await client.GetAsync(apiUrl, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    $"GitHub 저장소 {source.Owner}/{source.Repo}에 접근할 수 없습니다. " +
                    "Public Repository만 지원합니다.");
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (json.Contains("\"private\":true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GitHub 저장소 {source.Owner}/{source.Repo}는 Private Repository입니다. " +
                    "Public Repository만 지원합니다.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // API 호출 실패 시 clone 단계에서 자연스럽게 실패하도록 넘깁니다.
        }
    }

    private async Task<string> ResolveDefaultBranchAsync(
        string url,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var output = await ReadCheckedAsync(
            ["ls-remote", "--symref", url, "HEAD"],
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
        IEnumerable<string> arguments,
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
        IEnumerable<string> arguments,
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
}
