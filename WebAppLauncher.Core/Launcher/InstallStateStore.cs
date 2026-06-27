using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebAppLauncher.Core;

public sealed record InstallState(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("install_location")] string InstallLocation,
    [property: JsonPropertyName("setup_path")] string? SetupPath = null)
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
            var state = JsonSerializer.Deserialize<InstallState>(stream, JsonOptions);
            return state is not null &&
                   state.Format == InstallState.CurrentFormat &&
                   !string.IsNullOrWhiteSpace(state.Version) &&
                   !string.IsNullOrWhiteSpace(state.InstallLocation)
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
