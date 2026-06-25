using System.Globalization;
using System.Text;

namespace WebAppLauncher.Core;

public static class TomlManifestStore
{
    public static WapkManifest LoadWapk(string path)
    {
        var document = SimpleToml.ParseFile(path);
        var manifest = new WapkManifest(
            document.GetInt("wapk", "format"),
            ReadPackage(document),
            ReadSource(document),
            ReadRuntime(document),
            ReadWapkEntry(document),
            ReadWindow(document));
        ManifestValidator.Validate(manifest);
        return manifest;
    }

    public static WebAppManifest LoadWebApp(string path)
    {
        var document = SimpleToml.ParseFile(path);
        var manifest = new WebAppManifest(
            document.GetInt("webapp", "format"),
            DateTimeOffset.Parse(document.GetString("webapp", "installed_at"), CultureInfo.InvariantCulture),
            document.GetString("webapp", "source_commit"),
            ReadPackage(document),
            ReadPaths(document),
            ReadRuntime(document),
            ReadInstalledEntry(document),
            ReadNetwork(document),
            ReadStorage(document),
            ReadWindow(document));
        ManifestValidator.Validate(manifest);
        return manifest;
    }

    public static void SaveWebApp(string path, WebAppManifest manifest)
    {
        ManifestValidator.Validate(manifest);
        var builder = new StringBuilder();
        WriteSection(builder, "webapp");
        WriteValue(builder, "format", manifest.Format);
        WriteValue(builder, "installed_at", manifest.InstalledAt.ToString("O", CultureInfo.InvariantCulture));
        WriteValue(builder, "source_commit", manifest.SourceCommit);
        builder.AppendLine();

        WriteSection(builder, "package");
        WriteValue(builder, "id", manifest.Package.Id);
        WriteValue(builder, "name", manifest.Package.Name);
        WriteValue(builder, "version", manifest.Package.Version);
        builder.AppendLine();

        WriteSection(builder, "paths");
        WriteValue(builder, "source", manifest.Paths.Source);
        WriteValue(builder, "data", manifest.Paths.Data);
        WriteValue(builder, "logs", manifest.Paths.Logs);
        WriteValue(builder, "temp", manifest.Paths.Temp);
        builder.AppendLine();

        WriteSection(builder, "runtime");
        WriteValue(builder, "python", manifest.Runtime.Python);
        WriteValue(builder, "node", manifest.Runtime.Node);
        builder.AppendLine();

        WriteSection(builder, "entry");
        WriteValue(builder, "mode", manifest.Entry.Mode ?? "static");
        WriteValue(builder, "html", manifest.Entry.Html);
        WriteValue(builder, "server", manifest.Entry.Server ?? string.Empty);
        builder.AppendLine();

        WriteSection(builder, "network");
        WriteValue(builder, "host", manifest.Network.Host);
        WriteValue(builder, "port", manifest.Network.Port);
        WriteValue(builder, "origin", manifest.Network.Origin);
        builder.AppendLine();

        WriteSection(builder, "storage");
        WriteValue(builder, "browser_profile", manifest.Storage.BrowserProfile);
        WriteValue(builder, "pwa", manifest.Storage.Pwa);
        WriteValue(builder, "service_worker", manifest.Storage.ServiceWorker);
        builder.AppendLine();

        WriteSection(builder, "window");
        WriteValue(builder, "width", manifest.Window.Width);
        WriteValue(builder, "height", manifest.Window.Height);
        WriteValue(builder, "resizable", manifest.Window.Resizable);
        WriteValue(builder, "devtools", manifest.Window.Devtools);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static PackageInfo ReadPackage(SimpleToml document)
    {
        return new PackageInfo(
            document.GetString("package", "id"),
            document.GetString("package", "name"),
            document.GetString("package", "version"));
    }

    private static SourceInfo ReadSource(SimpleToml document)
    {
        return new SourceInfo(
            document.GetString("source", "provider"),
            document.GetString("source", "owner"),
            document.GetString("source", "repo"),
            document.GetString("source", "branch"),
            document.GetString("source", "commit"),
            document.GetString("source", "app_dir"));
    }

    private static RuntimeInfo ReadRuntime(SimpleToml document)
    {
        return new RuntimeInfo(
            document.GetString("runtime", "python"),
            document.GetString("runtime", "node"));
    }

    private static EntryInfo ReadWapkEntry(SimpleToml document)
    {
        return new EntryInfo(
            document.GetString("entry", "html"),
            document.GetOptionalString("entry", "python"),
            document.GetOptionalString("entry", "node"),
            document.GetOptionalString("entry", "icon"));
    }

    private static EntryInfo ReadInstalledEntry(SimpleToml document)
    {
        return new EntryInfo(
            document.GetString("entry", "html"),
            null,
            null,
            null,
            document.GetString("entry", "mode"),
            document.GetOptionalString("entry", "server"));
    }

    private static InstalledPaths ReadPaths(SimpleToml document)
    {
        return new InstalledPaths(
            document.GetString("paths", "source"),
            document.GetString("paths", "data"),
            document.GetString("paths", "logs"),
            document.GetString("paths", "temp"));
    }

    private static NetworkInfo ReadNetwork(SimpleToml document)
    {
        return new NetworkInfo(
            document.GetString("network", "host"),
            document.GetInt("network", "port"),
            document.GetString("network", "origin"));
    }

    private static StorageInfo ReadStorage(SimpleToml document)
    {
        return new StorageInfo(
            document.GetString("storage", "browser_profile"),
            document.GetBool("storage", "pwa"),
            document.GetBool("storage", "service_worker"));
    }

    private static WindowInfo ReadWindow(SimpleToml document)
    {
        return new WindowInfo(
            document.GetInt("window", "width"),
            document.GetInt("window", "height"),
            document.GetBool("window", "resizable"),
            document.GetBool("window", "devtools"));
    }

    private static void WriteSection(StringBuilder builder, string section)
    {
        builder.Append('[').Append(section).AppendLine("]");
    }

    private static void WriteValue(StringBuilder builder, string key, string value)
    {
        builder.Append(key).Append(" = \"").Append(Escape(value)).AppendLine("\"");
    }

    private static void WriteValue(StringBuilder builder, string key, int value)
    {
        builder.Append(key).Append(" = ").AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteValue(StringBuilder builder, string key, bool value)
    {
        builder.Append(key).Append(" = ").AppendLine(value ? "true" : "false");
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

internal sealed class SimpleToml
{
    private readonly Dictionary<string, Dictionary<string, string>> sections;

    private SimpleToml(Dictionary<string, Dictionary<string, string>> sections)
    {
        this.sections = sections;
    }

    public static SimpleToml ParseFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("TOML file was not found.", path);
        }

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = string.Empty;
        sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                throw new InvalidDataException($"Invalid TOML line: {rawLine}");
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            sections[current][key] = Unquote(value);
        }

        return new SimpleToml(sections);
    }

    public string GetString(string section, string key)
    {
        if (!sections.TryGetValue(section, out var table) ||
            !table.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing TOML field [{section}].{key}.");
        }

        return value.Trim();
    }

    public string? GetOptionalString(string section, string key)
    {
        if (!sections.TryGetValue(section, out var table) ||
            !table.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    public int GetInt(string section, string key)
    {
        var value = GetString(section, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new InvalidDataException($"TOML field [{section}].{key} must be an integer.");
    }

    public bool GetBool(string section, string key)
    {
        var value = GetString(section, key);
        return bool.TryParse(value, out var flag)
            ? flag
            : throw new InvalidDataException($"TOML field [{section}].{key} must be a boolean.");
    }

    private static string StripComment(string line)
    {
        var inString = false;
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && ch == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
        {
            var text = value[1..^1];
            return text.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return value;
    }
}
