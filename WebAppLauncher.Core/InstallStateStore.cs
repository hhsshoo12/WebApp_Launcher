using System.Text.Json;

namespace WebAppLauncher.Core;

public sealed record InstallState(
    int Format,
    string Product,
    string Version,
    string InstallLocation,
    string? SetupPath = null)
{
    public const int CurrentFormat = 2;
    public const string FileName = ".webapp-launcher-install.json";
}

public sealed class InstallStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Path { get; }

    public InstallStateStore(string fullPath)
    {
        Path = fullPath;
    }

    public static InstallStateStore ForDirectory(string installDirectory)
    {
        return new InstallStateStore(System.IO.Path.Combine(installDirectory, InstallState.FileName));
    }

    public InstallState? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize<InstallState>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public InstallState LoadOrDefault(string defaultVersion)
    {
        return Load() ?? new InstallState(
            InstallState.CurrentFormat,
            "WebApp Launcher",
            defaultVersion,
            System.IO.Path.GetDirectoryName(Path) ?? string.Empty);
    }

    public void Save(InstallState state)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
        }
        File.Move(tempPath, Path, overwrite: true);
    }
}
