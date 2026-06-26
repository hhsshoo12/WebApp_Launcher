namespace WebAppLauncher.Core;

public static class ManifestValidator
{
    private static readonly HashSet<string> PythonRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "python313",
        "python314"
    };

    private static readonly HashSet<string> NodeRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "nodejs-lts-22",
        "nodejs-lts-24"
    };

    public static void Validate(WapkManifest manifest)
    {
        if (manifest.Format is not (1 or 2))
        {
            throw new InvalidDataException("Unsupported .wapk format. Expected format = 1 or 2.");
        }

        ValidatePackage(manifest.Package, requireVersion: manifest.Format == 1);
        if (!string.Equals(manifest.Source.Provider, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only GitHub sources are supported in v1.");
        }

        Require(manifest.Source.Owner, "source.owner");
        Require(manifest.Source.Repo, "source.repo");
        Require(manifest.Source.Branch, "source.branch");
        Require(manifest.Source.Commit, "source.commit");
        EnsureRelativePath(manifest.Source.AppDir, "source.app_dir");
        ValidateRuntime(manifest.Runtime, allowLegacyPython312: false);
        EnsureRelativePath(manifest.Entry.Html, "entry.html");
        EnsureOptionalRelativePath(manifest.Entry.Python, "entry.python");
        EnsureOptionalRelativePath(manifest.Entry.Node, "entry.node");
        EnsureOptionalRelativePath(manifest.Entry.Icon, "entry.icon");
        ValidateWindow(manifest.Window);
    }

    public static void Validate(WebAppManifest manifest)
    {
        if (manifest.Format is not (1 or 2))
        {
            throw new InvalidDataException("Unsupported .webapp format. Expected format = 1 or 2.");
        }

        ValidatePackage(manifest.Package, requireVersion: true);
        Require(manifest.SourceCommit, "webapp.source_commit");
        ValidateRuntime(manifest.Runtime, allowLegacyPython312: true);
        EnsureRelativePath(manifest.Paths.Source, "paths.source");
        EnsureRelativePath(manifest.Paths.Data, "paths.data");
        EnsureRelativePath(manifest.Paths.Logs, "paths.logs");
        EnsureRelativePath(manifest.Paths.Temp, "paths.temp");
        EnsureRelativePath(manifest.Entry.Html, "entry.html");

        if (manifest.Entry.Mode is not ("static" or "server"))
        {
            throw new InvalidDataException("entry.mode must be 'static' or 'server'.");
        }

        EnsureOptionalRelativePath(manifest.Entry.Server, "entry.server");
        if (manifest.Network.Port != 0 &&
            manifest.Network.Port is < PortManager.FirstPort or > PortManager.LastPort)
        {
            throw new InvalidDataException("network.port must be 0 or in the legacy 52000..52999 range.");
        }

        if (manifest.Network.Host != "127.0.0.1")
        {
            throw new InvalidDataException("network.host must be 127.0.0.1 in v1.");
        }

        if (manifest.Storage.BrowserProfile is not ("ephemeral" or "persistent"))
        {
            throw new InvalidDataException("storage.browser_profile must be ephemeral.");
        }

        if (manifest.Storage.Pwa || manifest.Storage.ServiceWorker)
        {
            throw new InvalidDataException("PWA and Service Worker are not part of the v1 launcher contract.");
        }

        ValidateWindow(manifest.Window);
        if (manifest.Format == 2 && manifest.Source is null)
        {
            throw new InvalidDataException("format 2 .webapp metadata requires a source section.");
        }
    }

    private static void ValidatePackage(PackageInfo package, bool requireVersion)
    {
        Require(package.Id, "package.id");
        Require(package.Name, "package.name");
        if (requireVersion)
        {
            Require(package.Version, "package.version");
        }
        if (!package.Id.Contains('@', StringComparison.Ordinal))
        {
            throw new InvalidDataException("package.id must use owner@repo form.");
        }
    }

    private static void ValidateRuntime(RuntimeInfo runtime, bool allowLegacyPython312)
    {
        var isLegacyPython312 =
            allowLegacyPython312 &&
            runtime.Python.Equals("python312", StringComparison.OrdinalIgnoreCase);
        if (!PythonRuntimes.Contains(runtime.Python) && !isLegacyPython312)
        {
            throw new InvalidDataException("runtime.python must be one of none, python313, python314.");
        }

        if (!NodeRuntimes.Contains(runtime.Node))
        {
            throw new InvalidDataException("runtime.node must be one of none, nodejs-lts-22, nodejs-lts-24.");
        }
    }

    private static void ValidateWindow(WindowInfo window)
    {
        if (window.Width < 320 || window.Height < 240)
        {
            throw new InvalidDataException("window size is too small.");
        }

        if (window.InstanceMode is not ("focus_existing" or "share_backend" or "new_backend"))
        {
            throw new InvalidDataException(
                "window.instance_mode must be focus_existing, share_backend, or new_backend.");
        }
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{field} is required.");
        }
    }

    private static void EnsureOptionalRelativePath(string? value, string field)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            EnsureRelativePath(value, field);
        }
    }

    private static void EnsureRelativePath(string value, string field)
    {
        Require(value, field);
        if (Path.IsPathRooted(value) || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new InvalidDataException($"{field} must be a safe relative path.");
        }
    }
}
