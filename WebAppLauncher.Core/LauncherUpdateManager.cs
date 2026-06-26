using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebAppLauncher.Core;

public sealed record LauncherReleaseInfo(
    string Status,
    string InstalledVersion,
    string LatestVersion,
    string? DownloadUrl,
    string? ChecksumUrl,
    string? Message = null);

public sealed class LauncherUpdateManager
{
    public const string Repository = "hhsshoo12/WebApp_Launcher";
    public const string LauncherTagPrefix = "v";
    public const string LauncherAssetPattern = @"^WAPL-Launcher-v(?<version>[^/]+)\.zip$";

    private static readonly Regex AssetRegex = new(
        LauncherAssetPattern,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly InstallStateStore store;
    private readonly HttpClient client;

    public LauncherUpdateManager(InstallStateStore store, HttpClient? client = null)
    {
        this.store = store;
        this.client = client ?? new HttpClient();
        this.client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("WebAppLauncher", "0.1"));
    }

    public async Task<LauncherReleaseInfo> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var state = store.Load();
        if (state is null)
        {
            return new LauncherReleaseInfo(
                "no_state",
                string.Empty,
                string.Empty,
                null,
                null,
                "설치 상태 파일을 찾을 수 없습니다.");
        }

        try
        {
            using var response = await client.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page=30",
                cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                return new LauncherReleaseInfo(
                    "error",
                    state.Version,
                    string.Empty,
                    null,
                    null,
                    $"GitHub 응답 오류: {(int)response.StatusCode}");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new LauncherReleaseInfo(
                    "no_release",
                    state.Version,
                    string.Empty,
                    null,
                    null,
                    "GitHub 릴리스를 찾을 수 없습니다.");
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var release = document.RootElement.EnumerateArray()
                .Where(element => element.TryGetProperty("draft", out var draft) && !draft.GetBoolean())
                .Where(element => element.TryGetProperty("tag_name", out var tag) &&
                                  tag.GetString() is { } tagName &&
                                  tagName.StartsWith(LauncherTagPrefix, StringComparison.OrdinalIgnoreCase) &&
                                  !tagName.StartsWith(RuntimeUpdateManager.RuntimeTagPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(element => new
                {
                    TagName = element.GetProperty("tag_name").GetString() ?? string.Empty,
                    PublishedAt = element.TryGetProperty("published_at", out var published) &&
                                  published.ValueKind == JsonValueKind.String &&
                                  DateTimeOffset.TryParse(published.GetString(), out var parsed)
                        ? parsed
                        : DateTimeOffset.MinValue,
                    Assets = element.GetProperty("assets").EnumerateArray()
                        .Select(asset => new
                        {
                            Name = asset.GetProperty("name").GetString() ?? string.Empty,
                            Url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty
                        })
                        .ToArray()
                })
                .OrderByDescending(release => release.PublishedAt)
                .FirstOrDefault();

            if (release is null)
            {
                return new LauncherReleaseInfo(
                    "no_release",
                    state.Version,
                    string.Empty,
                    null,
                    null,
                    $"{LauncherTagPrefix}* 런처 릴리스가 없습니다.");
            }

            var zip = release.Assets
                .Select(asset => new { Asset = asset, Match = AssetRegex.Match(asset.Name) })
                .FirstOrDefault(value => value.Match.Success);

            if (zip is null)
            {
                return new LauncherReleaseInfo(
                    "no_release",
                    state.Version,
                    string.Empty,
                    null,
                    null,
                    "WAPL-Launcher-v*.zip 자산을 찾을 수 없습니다.");
            }

            var checksum = release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals(zip.Asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (checksum is null)
            {
                return new LauncherReleaseInfo(
                    "no_release",
                    state.Version,
                    zip.Match.Groups["version"].Value,
                    zip.Asset.Url,
                    null,
                    "체크섬 자산을 찾을 수 없습니다.");
            }

            var latestVersion = zip.Match.Groups["version"].Value;
            var status = state.Version.Equals(latestVersion, StringComparison.OrdinalIgnoreCase)
                ? "current"
                : "available";
            return new LauncherReleaseInfo(
                status,
                state.Version,
                latestVersion,
                zip.Asset.Url,
                checksum.Url);
        }
        catch (Exception ex)
        {
            return new LauncherReleaseInfo(
                "error",
                state?.Version ?? string.Empty,
                string.Empty,
                null,
                null,
                ex.Message);
        }
    }

    public InstallState? GetInstallState() => store.Load();

    public Process? TriggerUpdate(
        InstallState state,
        IReadOnlyList<string>? extraArgs = null)
    {
        if (string.IsNullOrWhiteSpace(state.SetupPath) || !File.Exists(state.SetupPath))
        {
            throw new FileNotFoundException(
                "저장된 Setup.exe를 찾을 수 없습니다. 설치 관리자로 다시 설치해야 합니다.",
                state.SetupPath ?? "(null)");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = state.SetupPath,
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("--update");
        startInfo.ArgumentList.Add("--install-dir");
        startInfo.ArgumentList.Add(state.InstallLocation);
        if (extraArgs is not null)
        {
            foreach (var arg in extraArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        return Process.Start(startInfo);
    }
}
