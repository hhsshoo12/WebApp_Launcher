using System.Globalization;
using System.Text;

namespace WebAppLauncher.Core;

public static class TomlManifestStore
{
    public static WapkManifest LoadWapk(string path)
    {
        var document = SimpleToml.ParseFile(path);
        var format = document.GetInt("wapk", "format");
        var manifest = new WapkManifest(
            format,
            ReadPackage(document, versionRequired: false),
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
            ReadWindow(document),
            ReadOptionalSource(document));
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

        if (manifest.Source is not null)
        {
            WriteSection(builder, "source");
            WriteValue(builder, "provider", manifest.Source.Provider);
            WriteValue(builder, "owner", manifest.Source.Owner);
            WriteValue(builder, "repo", manifest.Source.Repo);
            WriteValue(builder, "branch", manifest.Source.Branch);
            WriteValue(builder, "commit", manifest.Source.Commit);
            WriteValue(builder, "app_dir", manifest.Source.AppDir);
            builder.AppendLine();
        }

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
        WriteValue(builder, "icon", manifest.Entry.Icon ?? string.Empty);
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
        WriteValue(builder, "transparent", manifest.Window.Transparent);
        WriteValue(builder, "borderless", manifest.Window.Borderless);
        WriteValue(builder, "fullscreen", manifest.Window.Fullscreen);
        WriteValue(builder, "always_on_top", manifest.Window.AlwaysOnTop);
        WriteValue(builder, "start_maximized", manifest.Window.StartMaximized);
        WriteValue(builder, "instance_mode", manifest.Window.InstanceMode);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static PackageInfo ReadPackage(SimpleToml document, bool versionRequired = true)
    {
        return new PackageInfo(
            document.GetString("package", "id"),
            document.GetString("package", "name"),
            versionRequired
                ? document.GetString("package", "version")
                : document.GetOptionalString("package", "version") ?? string.Empty);
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

    private static SourceInfo? ReadOptionalSource(SimpleToml document)
    {
        var owner = document.GetOptionalString("source", "owner");
        var repo = document.GetOptionalString("source", "repo");
        if (owner is null || repo is null)
        {
            return null;
        }

        return new SourceInfo(
            document.GetOptionalString("source", "provider") ?? "github",
            owner,
            repo,
            document.GetOptionalString("source", "branch") ?? "*",
            document.GetOptionalString("source", "commit") ?? "*",
            document.GetOptionalString("source", "app_dir") ?? ".");
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
            document.GetOptionalString("entry", "icon"),
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
            document.GetBool("window", "devtools"),
            document.GetOptionalBool("window", "transparent"),
            document.GetOptionalBool("window", "borderless"),
            document.GetOptionalBool("window", "fullscreen"),
            document.GetOptionalBool("window", "always_on_top"),
            document.GetOptionalBool("window", "start_maximized"),
            document.GetOptionalString("window", "instance_mode") ?? "new_backend");
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
