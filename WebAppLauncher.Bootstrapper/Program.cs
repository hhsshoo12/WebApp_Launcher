using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebAppLauncher.Core;

return await Bootstrapper.RunAsync(args);

internal static class Bootstrapper
{
    private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            if (command == "webview2-status")
            {
                var version = GetInstalledWebView2Version();
                Console.WriteLine(version is null ? "WebView2: not installed" : $"WebView2: {version}");
                return version is null ? 2 : 0;
            }

            var root = GetOption(args, "--root");
            var paths = new WebAppPaths(root);
            paths.EnsureRootLayout();

            if (command == "apply-runtime")
            {
                var staging = GetOption(args, "--staging")
                    ?? throw new InvalidOperationException("apply-runtime requires --staging <path>.");
                var waitPidText = GetOption(args, "--wait-pid")
                    ?? throw new InvalidOperationException("apply-runtime requires --wait-pid <pid>.");
                var restart = GetOption(args, "--restart")
                    ?? throw new InvalidOperationException("apply-runtime requires --restart <path>.");
                if (!int.TryParse(waitPidText, out var waitPid))
                {
                    throw new InvalidOperationException("--wait-pid must be an integer.");
                }

                await ApplyRuntimeUpdateAsync(paths, staging, waitPid, restart);
                return 0;
            }

            if (command is "install" or "all")
            {
                var catalog = GetOption(args, "--catalog");
                var catalogData = catalog is null ? null : RuntimeCatalog.Load(catalog);
                var totalStages = 1 + (catalogData?.Items.Count ?? 0);
                await InstallWebView2Async(0, totalStages);
                if (catalog is not null)
                {
                    await InstallCatalogAsync(paths, catalogData!, 1, totalStages);
                }

                ReportProgress("complete", "bootstrap", totalStages, totalStages, 100, "설치 환경 준비 완료");
                Console.WriteLine($"Bootstrap complete: {paths.Root}");
                return 0;
            }

            if (command == "catalog")
            {
                var catalog = GetOption(args, "--catalog") ?? throw new InvalidOperationException("catalog requires --catalog <path>.");
                var catalogData = RuntimeCatalog.Load(catalog);
                await InstallCatalogAsync(paths, catalogData, 0, catalogData.Items.Count);
                Console.WriteLine($"Catalog install complete: {paths.Root}");
                return 0;
            }

            throw new InvalidOperationException($"Unknown command: {args[0]}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task InstallWebView2Async(int stageIndex, int totalStages)
    {
        var installedVersion = GetInstalledWebView2Version();
        if (!string.IsNullOrWhiteSpace(installedVersion))
        {
            Console.WriteLine($"Skip existing WebView2: {installedVersion}");
            ReportStageProgress("skip", "WebView2", stageIndex, totalStages, 100, $"이미 설치됨 ({installedVersion})");
            return;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"MicrosoftEdgeWebView2Setup-{Guid.NewGuid():N}.exe");
        try
        {
            using var client = new HttpClient();
            await DownloadFileAsync(
                client,
                new Uri(WebView2BootstrapperUrl),
                tempFile,
                "WebView2",
                stageIndex,
                totalStages,
                0,
                70);

            ReportStageProgress("install", "WebView2", stageIndex, totalStages, 70, "WebView2 설치 중");
            var startInfo = new ProcessStartInfo(tempFile, "/silent /install")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start WebView2 installer.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"WebView2 installer failed with exit code {process.ExitCode}.");
            }
            ReportStageProgress("install", "WebView2", stageIndex, totalStages, 100, "WebView2 설치 완료");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static async Task ApplyRuntimeUpdateAsync(
        WebAppPaths paths,
        string staging,
        int waitPid,
        string restart)
    {
        try
        {
            using var process = Process.GetProcessById(waitPid);
            await process.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
        }

        var stagingRoot = Path.GetFullPath(staging);
        var updatesRoot = Path.GetFullPath(paths.RuntimeUpdates) + Path.DirectorySeparatorChar;
        if (!stagingRoot.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Runtime staging path is outside the update directory.");
        }

        foreach (var required in new[] { "runtime", "tools", "runtime-manifest.toml", "LICENSES" })
        {
            var candidate = Path.Combine(stagingRoot, required);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                throw new InvalidDataException($"Runtime staging is missing {required}.");
            }
        }

        var backup = Path.Combine(paths.RuntimeUpdates, $"backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        var applied = false;
        try
        {
            MoveExisting(paths.Runtime, Path.Combine(backup, "runtime"));
            MoveExisting(paths.Tools, Path.Combine(backup, "tools"));
            MoveExisting(
                Path.Combine(paths.Root, "runtime-manifest.toml"),
                Path.Combine(backup, "runtime-manifest.toml"));
            MoveExisting(
                Path.Combine(paths.Root, "LICENSES"),
                Path.Combine(backup, "LICENSES"));

            Directory.Move(Path.Combine(stagingRoot, "runtime"), paths.Runtime);
            Directory.Move(Path.Combine(stagingRoot, "tools"), paths.Tools);
            File.Move(
                Path.Combine(stagingRoot, "runtime-manifest.toml"),
                Path.Combine(paths.Root, "runtime-manifest.toml"));
            Directory.Move(
                Path.Combine(stagingRoot, "LICENSES"),
                Path.Combine(paths.Root, "LICENSES"));
            applied = true;
        }
        finally
        {
            if (!applied)
            {
                DeleteIfExists(paths.Runtime);
                DeleteIfExists(paths.Tools);
                DeleteIfExists(Path.Combine(paths.Root, "runtime-manifest.toml"));
                DeleteIfExists(Path.Combine(paths.Root, "LICENSES"));
                RestoreBackup(Path.Combine(backup, "runtime"), paths.Runtime);
                RestoreBackup(Path.Combine(backup, "tools"), paths.Tools);
                RestoreBackup(
                    Path.Combine(backup, "runtime-manifest.toml"),
                    Path.Combine(paths.Root, "runtime-manifest.toml"));
                RestoreBackup(Path.Combine(backup, "LICENSES"), Path.Combine(paths.Root, "LICENSES"));
            }
        }

        if (Directory.Exists(backup))
        {
            try
            {
                Directory.Delete(backup, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Process.Start(new ProcessStartInfo(restart)
        {
            WorkingDirectory = Path.GetDirectoryName(restart)!,
            UseShellExecute = true
        });
    }

    private static void MoveExisting(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    private static void RestoreBackup(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string? GetInstalledWebView2Version()
    {
        try
        {
            var result = GetAvailableCoreWebView2BrowserVersionString(null, out var versionPointer);
            if (result != 0 || versionPointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(versionPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(versionPointer);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetAvailableCoreWebView2BrowserVersionString(
        string? browserExecutableFolder,
        out IntPtr versionInfo);

    private static async Task InstallCatalogAsync(
        WebAppPaths paths,
        RuntimeCatalog catalog,
        int firstStageIndex,
        int totalStages)
    {
        using var client = new HttpClient();
        for (var itemIndex = 0; itemIndex < catalog.Items.Count; itemIndex++)
        {
            var item = catalog.Items[itemIndex];
            var stageIndex = firstStageIndex + itemIndex;
            var destination = SafeCombine(paths.Root, item.Destination);
            if (IsBundleInstalled(paths, item))
            {
                Console.WriteLine($"Skip existing {item.Name}");
                ReportStageProgress("skip", item.Name, stageIndex, totalStages, 100, "이미 설치됨");
                continue;
            }

            if (item.Kind == RuntimeCatalogItemKind.Bundle)
            {
                Directory.CreateDirectory(paths.Root);
            }
            else
            {
                Directory.CreateDirectory(destination);
            }

            var extension = Path.GetExtension(item.Url.LocalPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = item.Type.Equals("python-installer", StringComparison.OrdinalIgnoreCase) ||
                            item.Type.Equals("7z-sfx", StringComparison.OrdinalIgnoreCase)
                    ? ".exe"
                    : ".download";
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"{item.Name}-{Guid.NewGuid():N}{extension}");
            try
            {
                Console.WriteLine($"Download {item.Name}");
                await DownloadFileAsync(client, item.Url, tempFile, item.Name, stageIndex, totalStages, 0, 70);
                await InstallItemAsync(item, tempFile, destination, stageIndex, totalStages);
                ReportStageProgress("item-complete", item.Name, stageIndex, totalStages, 100, "설치 완료");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
    }

    private static bool IsBundleInstalled(WebAppPaths paths, RuntimeCatalogItem item)
    {
        if (item.Kind != RuntimeCatalogItemKind.Bundle)
        {
            var target = SafeCombine(paths.Root, item.Destination);
            return Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any();
        }

        return File.Exists(Path.Combine(paths.Root, "runtime-manifest.toml")) &&
               Directory.Exists(Path.Combine(paths.Root, "runtime")) &&
               Directory.EnumerateFileSystemEntries(Path.Combine(paths.Root, "runtime")).Any() &&
               Directory.Exists(Path.Combine(paths.Root, "tools")) &&
               Directory.EnumerateFileSystemEntries(Path.Combine(paths.Root, "tools")).Any();
    }

    private static async Task InstallItemAsync(
        RuntimeCatalogItem item,
        string downloadedFile,
        string destination,
        int stageIndex,
        int totalStages)
    {
        switch (item.Type.ToLowerInvariant())
        {
            case "bundle-zip":
                ReportStageProgress("verify", item.Name, stageIndex, totalStages, 75, "체크섬 확인 중");
                VerifySha256(downloadedFile, item, item.Name);
                await ExtractBundleAsync(downloadedFile, destination, item.Name, stageIndex, totalStages);
                break;

            case "zip":
                await ExtractZipAsync(downloadedFile, destination, item.Name, stageIndex, totalStages);
                FlattenSingleRootDirectory(destination);
                break;

            case "python-installer":
                ReportStageProgress("install", item.Name, stageIndex, totalStages, 70, "설치 프로그램 실행 중");
                await RunInstallerAsync(
                    downloadedFile,
                    [
                        "/quiet",
                        "InstallAllUsers=0",
                        $"TargetDir={destination}",
                        "Include_launcher=0",
                        "PrependPath=0",
                        "Shortcuts=0",
                        "Include_test=0"
                    ]);
                break;

            case "7z-sfx":
                ReportStageProgress("extract", item.Name, stageIndex, totalStages, 70, "압축 해제 중");
                await RunInstallerAsync(downloadedFile, ["-y", $"-o{destination}"]);
                FlattenSingleRootDirectory(destination);
                break;

            case "file":
                ReportStageProgress("copy", item.Name, stageIndex, totalStages, 70, "파일 배치 중");
                var fileName = Path.GetFileName(item.Url.LocalPath);
                File.Copy(downloadedFile, Path.Combine(destination, fileName), overwrite: true);
                break;

            default:
                throw new InvalidDataException($"Unsupported catalog archive type '{item.Type}' for {item.Name}.");
        }
    }

    private static void VerifySha256(string downloadedFile, RuntimeCatalogItem item, string itemName)
    {
        if (string.IsNullOrWhiteSpace(item.Sha256))
        {
            throw new InvalidDataException(
                $"Catalog item '{itemName}' is missing a sha256 entry; refusing to install an unverified bundle.");
        }

        var expected = item.Sha256.Trim().ToLowerInvariant();
        if (expected.Length != 64 || !Regex.IsMatch(expected, "^[0-9a-f]{64}$"))
        {
            throw new InvalidDataException(
                $"Catalog item '{itemName}' has an invalid sha256 value: {item.Sha256}");
        }

        using var stream = File.OpenRead(downloadedFile);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{itemName} SHA-256 verification failed (expected {expected}, got {actual}).");
        }
    }

    private static async Task ExtractBundleAsync(
        string archivePath,
        string destinationRoot,
        string itemName,
        int stageIndex,
        int totalStages)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"{itemName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ReportStageProgress("extract", itemName, stageIndex, totalStages, 80, "압축 해제 중");
            await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true));
            ValidateBundleLayout(staging);
            ReportStageProgress("apply", itemName, stageIndex, totalStages, 92, "런타임 배치 중");
            await Task.Run(() => MoveDirectoryContents(staging, destinationRoot));
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void ValidateBundleLayout(string staging)
    {
        foreach (var required in new[] { "runtime", "tools", "LICENSES", "runtime-manifest.toml" })
        {
            var path = Path.Combine(staging, required);
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                throw new InvalidDataException(
                    $"Runtime bundle is missing required entry '{required}'.");
            }
        }
    }

    private static void MoveDirectoryContents(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(destination, name);
            if (Directory.Exists(entry))
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(entry, target);
            }
            else
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(entry, target);
            }
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        Uri uri,
        string destination,
        string itemName,
        int stageIndex,
        int totalStages,
        double stageStartPercent,
        double stageEndPercent)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(destination);
        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        var lastPercent = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            var percent = totalBytes > 0
                ? Math.Clamp((int)(downloaded * 100 / totalBytes), 0, 100)
                : -1;
            if (percent != lastPercent)
            {
                var stagePercent = percent >= 0
                    ? stageStartPercent + ((stageEndPercent - stageStartPercent) * percent / 100)
                    : stageStartPercent;
                ReportProgress(
                    "download",
                    itemName,
                    downloaded,
                    totalBytes,
                    CalculateOverallPercent(stageIndex, totalStages, stagePercent),
                    "다운로드 중");
                lastPercent = percent;
            }
        }

        ReportProgress(
            "download",
            itemName,
            downloaded,
            totalBytes > 0 ? totalBytes : downloaded,
            CalculateOverallPercent(stageIndex, totalStages, stageEndPercent),
            "다운로드 완료");
    }

    private static async Task ExtractZipAsync(
        string archivePath,
        string destination,
        string itemName,
        int stageIndex,
        int totalStages)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries;
        var totalEntries = entries.Count;
        var completedEntries = 0;

        foreach (var entry in entries)
        {
            var destinationPath = SafeCombine(destination, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var source = entry.Open();
                await using var target = File.Create(destinationPath);
                await source.CopyToAsync(target);
            }

            completedEntries++;
            var extractionPercent = totalEntries == 0 ? 100 : completedEntries * 100.0 / totalEntries;
            var stagePercent = 70 + (30 * extractionPercent / 100);
            ReportProgress(
                "extract",
                itemName,
                completedEntries,
                totalEntries,
                CalculateOverallPercent(stageIndex, totalStages, stagePercent),
                "압축 해제 중");
        }
    }

    private static void ReportStageProgress(
        string phase,
        string item,
        int stageIndex,
        int totalStages,
        double stagePercent,
        string message)
    {
        ReportProgress(
            phase,
            item,
            (long)Math.Round(stagePercent),
            100,
            CalculateOverallPercent(stageIndex, totalStages, stagePercent),
            message);
    }

    private static double CalculateOverallPercent(int stageIndex, int totalStages, double stagePercent)
    {
        if (totalStages <= 0)
        {
            return 100;
        }

        return Math.Clamp(((stageIndex + (stagePercent / 100)) / totalStages) * 100, 0, 100);
    }

    private static void ReportProgress(
        string phase,
        string item,
        long current,
        long total,
        double overallPercent,
        string message)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "progress",
            phase,
            item,
            current,
            total,
            overallPercent = Math.Round(overallPercent, 1),
            message
        });
        Console.WriteLine($"@@WEBAPP_PROGRESS {payload}");
    }

    private static async Task RunInstallerAsync(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}.");
        }
    }

    private static void FlattenSingleRootDirectory(string destination)
    {
        var files = Directory.EnumerateFiles(destination).Take(1).ToList();
        var directories = Directory.EnumerateDirectories(destination).ToList();
        if (files.Count > 0 || directories.Count != 1)
        {
            return;
        }

        var inner = directories[0];
        var temp = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".flattening";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        Directory.Move(inner, temp);
        Directory.Delete(destination, recursive: true);
        Directory.Move(temp, destination);
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Catalog destination escapes the .webapp root.");
        }

        return fullPath;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        WebAppLauncher.Bootstrapper

        Commands:
          install [--catalog <runtime-catalog.toml>] [--root <path>]
          catalog --catalog <runtime-catalog.toml> [--root <path>]
          apply-runtime --staging <path> --wait-pid <pid> --restart <launcher.exe> [--root <path>]
          webview2-status

        Catalog format (single runtime bundle hosted on GitHub Releases):
          [bundle.runtime]
          type = "bundle-zip"
          url = "https://github.com/.../WAPL-Runtime-v0.1.zip"
          sha256 = "<64-char hex digest>"
          destination = "."
        """);
    }
}

internal enum RuntimeCatalogItemKind
{
    Archive,
    Bundle,
}

internal sealed record RuntimeCatalogItem(
    string Name,
    string Type,
    Uri Url,
    string Destination,
    string? Sha256,
    RuntimeCatalogItemKind Kind);

internal sealed class RuntimeCatalog
{
    public RuntimeCatalog(IReadOnlyList<RuntimeCatalogItem> items)
    {
        Items = items;
    }

    public IReadOnlyList<RuntimeCatalogItem> Items { get; }

    public static RuntimeCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Runtime catalog was not found.", path);
        }

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = string.Empty;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                current = line[1..^1].Trim();
                sections.TryAdd(current, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new InvalidDataException($"Invalid catalog line: {rawLine}");
            }

            sections[current][line[..equals].Trim()] = Unquote(line[(equals + 1)..].Trim());
        }

        var items = new List<RuntimeCatalogItem>();
        foreach (var pair in sections)
        {
            var kind = pair.Key.StartsWith("bundle.", StringComparison.OrdinalIgnoreCase)
                ? RuntimeCatalogItemKind.Bundle
                : pair.Key.StartsWith("archive.", StringComparison.OrdinalIgnoreCase)
                    ? RuntimeCatalogItemKind.Archive
                    : (RuntimeCatalogItemKind?)null;
            if (kind is null)
            {
                continue;
            }

            var prefix = kind == RuntimeCatalogItemKind.Bundle ? "bundle." : "archive.";
            var name = pair.Key[prefix.Length..];
            var type = GetOptional(pair.Value, "type") ?? "zip";
            var url = Get(pair.Value, "url", pair.Key);
            var destination = GetOptional(pair.Value, "destination") ?? ".";
            var sha256 = GetOptional(pair.Value, "sha256");
            items.Add(new RuntimeCatalogItem(name, type, new Uri(url), destination, sha256, kind.Value));
        }

        return new RuntimeCatalog(items);
    }

    private static string Get(Dictionary<string, string> table, string key, string section)
    {
        return table.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Missing [{section}].{key}.");
    }

    private static string? GetOptional(Dictionary<string, string> table, string key)
    {
        return table.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        return value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)
            ? value[1..^1]
            : value;
    }
}
