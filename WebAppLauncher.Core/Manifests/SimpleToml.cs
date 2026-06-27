using System.Globalization;

namespace WebAppLauncher.Core;

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

    public IEnumerable<string> GetSectionKeys(string section)
    {
        if (sections.TryGetValue(section, out var table))
        {
            return table.Keys;
        }

        return Array.Empty<string>();
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

    public bool GetOptionalBool(string section, string key, bool defaultValue = false)
    {
        if (!sections.TryGetValue(section, out var table) ||
            !table.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

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
