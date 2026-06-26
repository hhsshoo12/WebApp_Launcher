using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebAppLauncher.Core;

public sealed record RuntimeBundleInfo(
    string Version,
    string ZipUrl,
    string ChecksumUrl,
    string Status,
    string? InstalledVersion = null,
    string? StagingDirectory = null,
    string? Message = null);

public sealed class RuntimeUpdateManager
{
    public const string Repository = "hhsshoo12/WebApp_Launcher";
    public const string RuntimeTagPrefix = "runtime-";
    private static readonly Regex AssetPattern = new(
        @"^WAPL-Runtime-v(?<version>[^/]+)\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly WebAppPaths paths;
    private readonly HttpClient client;

    public RuntimeUpdateManager(WebAppPaths paths, HttpClient? client = null)
    {
        this.paths = paths;
        this.client = client ?? new HttpClient();
        this.client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("WebAppLauncher", "0.1"));
    }

    public async Task<RuntimeBundleInfo> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page=30",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new RuntimeBundleInfo(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "no_release",
                    ReadInstalledVersion(),
                    Message: "Runtime release was not found.");
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var release = document.RootElement.EnumerateArray()
                .Where(element => element.TryGetProperty("draft", out var draft) && !draft.GetBoolean())
                .Where(element => element.TryGetProperty("tag_name", out var tag) &&
                                  tag.GetString() is { } tagName &&
                                  tagName.StartsWith(RuntimeTagPrefix, StringComparison.OrdinalIgnoreCase))
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
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"No '{RuntimeTagPrefix}*' release with a WAPL-Runtime-v*.zip asset was found.");

            var zip = release.Assets
                .Select(asset => new { Asset = asset, Match = AssetPattern.Match(asset.Name) })
                .FirstOrDefault(value => value.Match.Success)
                ?? throw new InvalidOperationException("WAPL-Runtime-v*.zip release asset was not found.");
            var checksum = release.Assets.FirstOrDefault(asset =>
                    asset.Name.Equals(zip.Asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{zip.Asset.Name}.sha256 release asset was not found.");
            var installed = ReadInstalledVersion();
            var status = installed is not null &&
                         installed.Equals(zip.Match.Groups["version"].Value, StringComparison.OrdinalIgnoreCase)
                ? "current"
                : "available";
            return new RuntimeBundleInfo(
                zip.Match.Groups["version"].Value,
                zip.Asset.Url,
                checksum.Url,
                status,
                installed);
        }
        catch (Exception ex)
        {
            return new RuntimeBundleInfo(
                string.Empty,
                string.Empty,
                string.Empty,
                "error",
                ReadInstalledVersion(),
                Message: ex.Message);
        }
    }

    public async Task<RuntimeBundleInfo> DownloadAsync(
        RuntimeBundleInfo bundle,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (bundle.Status == "error" || string.IsNullOrWhiteSpace(bundle.ZipUrl))
        {
            throw new InvalidOperationException(bundle.Message ?? "Runtime update information is invalid.");
        }

        Directory.CreateDirectory(paths.RuntimeUpdates);
        var safeVersion = WebAppPaths.SanitizeSegment(bundle.Version);
        var work = Path.Combine(paths.RuntimeUpdates, WebAppPaths.SanitizeSegment($"v{bundle.Version}"));
        var staging = Path.Combine(work, "staging");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        try
        {
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, $"WAPL-Runtime-v{safeVersion}.zip");
            await DownloadAsync(bundle.ZipUrl, zipPath, progress, cancellationToken);
            var checksumText = await client.GetStringAsync(bundle.ChecksumUrl, cancellationToken);
            var expected = Regex.Match(checksumText, @"\b[a-fA-F0-9]{64}\b").Value;
            if (expected.Length != 64)
            {
                throw new InvalidDataException("Runtime checksum file does not contain a SHA-256 value.");
            }

            await using (var stream = File.OpenRead(zipPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Runtime archive SHA-256 verification failed.");
                }
            }

            ExtractSafely(zipPath, staging);
            ValidateStaging(staging);
            return bundle with { Status = "downloaded", StagingDirectory = staging };
        }
        catch
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            throw;
        }
    }

    public string? ReadInstalledVersion()
    {
        var manifestPath = Path.Combine(paths.Root, "runtime-manifest.toml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return SimpleToml.ParseFile(manifestPath).GetString("runtime", "bundle_version");
        }
        catch
        {
            return null;
        }
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        var buffer = new byte[128 * 1024];
        long current = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            current += read;
            if (total > 0)
            {
                progress?.Report(current * 100d / total.Value);
            }
        }
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Runtime archive contains a path traversal entry.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void ValidateStaging(string staging)
    {
        if (!File.Exists(Path.Combine(staging, "runtime-manifest.toml")))
        {
            throw new InvalidDataException("Runtime archive is missing runtime-manifest.toml.");
        }

        foreach (var required in new[] { "runtime", "tools", "LICENSES" })
        {
            var path = Path.Combine(staging, required);
            if (!Directory.Exists(path))
            {
                throw new InvalidDataException($"Runtime archive is missing {required}.");
            }
        }

        _ = SimpleToml.ParseFile(Path.Combine(staging, "runtime-manifest.toml"))
            .GetString("runtime", "bundle_version");
    }
}
