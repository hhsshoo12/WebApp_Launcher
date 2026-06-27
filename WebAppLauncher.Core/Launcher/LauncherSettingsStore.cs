using System.Text.Json;

namespace WebAppLauncher.Core;

public sealed record LauncherSettings(
    bool DeveloperMode = false,
    bool AutomaticAppUpdates = true,
    DateTimeOffset? LastAppUpdateCheck = null);

public sealed class LauncherSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string path;

    public LauncherSettingsStore(WebAppPaths paths)
    {
        path = Path.Combine(paths.Root, "launcher-settings.json");
    }

    public LauncherSettings Load()
    {
        if (!File.Exists(path))
        {
            return new LauncherSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), JsonOptions)
                ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
