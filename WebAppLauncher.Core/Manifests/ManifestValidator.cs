using System.Text.RegularExpressions;

namespace WebAppLauncher.Core;

public static class ManifestValidator
{
    private static readonly Regex GitHubSegmentPattern = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$",
        RegexOptions.Compiled);
    private static readonly Regex GitCommitPattern = new(
        @"^[a-fA-F0-9]{7,40}$",
        RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> PythonRuntimes;
    private static readonly HashSet<string> NodeRuntimes;

    static ManifestValidator()
    {
        var webappRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".webapp");
        var manifestPath = Path.Combine(webappRoot, "runtime-manifest.toml");
        
        var pythonList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "none" };
        var nodeList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "none" };

        if (File.Exists(manifestPath))
        {
            try
            {
                var document = SimpleToml.ParseFile(manifestPath);
                foreach (var id in document.GetSectionKeys("versions"))
                {
                    if (id.StartsWith("python", StringComparison.OrdinalIgnoreCase))
                    {
                        pythonList.Add(id);
                    }
                    else if (id.StartsWith("nodejs-lts-", StringComparison.OrdinalIgnoreCase) ||
                             id.StartsWith("node", StringComparison.OrdinalIgnoreCase))
                    {
                        nodeList.Add(id);
                    }
                }
            }
            catch
            {
                PopulateDefaults(pythonList, nodeList);
            }
        }
        else
        {
            PopulateDefaults(pythonList, nodeList);
        }

        PythonRuntimes = pythonList;
        NodeRuntimes = nodeList;
    }

    private static void PopulateDefaults(HashSet<string> pythonList, HashSet<string> nodeList)
    {
        pythonList.Add("python313");
        pythonList.Add("python314");
        nodeList.Add("nodejs-lts-22");
        nodeList.Add("nodejs-lts-24");
    }

    public static void Validate(WapkManifest manifest)
    {
        if (manifest.Format != 2)
        {
            throw new InvalidDataException("Unsupported .wapk format. Expected format = 2.");
        }

        ValidatePackage(manifest.Package, requireVersion: false);
        if (!string.Equals(manifest.Source.Provider, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only GitHub sources are supported in v1.");
        }

        Require(manifest.Source.Owner, "source.owner");
        Require(manifest.Source.Repo, "source.repo");
        Require(manifest.Source.Branch, "source.branch");
        Require(manifest.Source.Commit, "source.commit");
        ValidateGitHubSegment(manifest.Source.Owner, "source.owner");
        ValidateGitHubSegment(manifest.Source.Repo, "source.repo");
        ValidateBranch(manifest.Source.Branch);
        ValidateCommit(manifest.Source.Commit);
        if (!manifest.Package.Id.Equals(
                $"{manifest.Source.Owner}@{manifest.Source.Repo}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("package.id must match source.owner@source.repo.");
        }

        EnsureRelativePath(manifest.Source.AppDir, "source.app_dir");
        ValidateRuntime(manifest.Runtime);
        EnsureRelativePath(manifest.Entry.Html, "entry.html");
        EnsureOptionalRelativePath(manifest.Entry.Python, "entry.python");
        EnsureOptionalRelativePath(manifest.Entry.Node, "entry.node");
        EnsureOptionalRelativePath(manifest.Entry.Icon, "entry.icon");
        ValidateWindow(manifest.Window);
    }

    public static void Validate(WebAppManifest manifest)
    {
        if (manifest.Format != 2)
        {
            throw new InvalidDataException("Unsupported .webapp format. Expected format = 2.");
        }

        ValidatePackage(manifest.Package, requireVersion: true);
        Require(manifest.SourceCommit, "webapp.source_commit");
        ValidateRuntime(manifest.Runtime);
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
        if (manifest.Network.Port != 0)
        {
            throw new InvalidDataException("network.port must be 0.");
        }

        if (manifest.Network.Host != "127.0.0.1")
        {
            throw new InvalidDataException("network.host must be 127.0.0.1 in v1.");
        }

        if (!manifest.Storage.BrowserProfile.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("storage.browser_profile must be ephemeral.");
        }

        if (manifest.Storage.Pwa || manifest.Storage.ServiceWorker)
        {
            throw new InvalidDataException("PWA and Service Worker are not part of the v1 launcher contract.");
        }

        ValidateWindow(manifest.Window);
        if (manifest.Source is null)
        {
            throw new InvalidDataException(".webapp metadata requires a source section.");
        }

        ValidateGitHubSegment(manifest.Source.Owner, "source.owner");
        ValidateGitHubSegment(manifest.Source.Repo, "source.repo");
        ValidateBranch(manifest.Source.Branch);
        ValidateCommit(manifest.Source.Commit);
    }

    private static void ValidatePackage(PackageInfo package, bool requireVersion)
    {
        Require(package.Id, "package.id");
        Require(package.Name, "package.name");
        var packageParts = package.Id.Split('@');
        if (packageParts.Length != 2)
        {
            throw new InvalidDataException("package.id must use owner@repo form.");
        }

        ValidateGitHubSegment(packageParts[0], "package.id owner");
        ValidateGitHubSegment(packageParts[1], "package.id repo");
        if (requireVersion)
        {
            Require(package.Version, "package.version");
            if (!VersionPattern.IsMatch(package.Version))
            {
                throw new InvalidDataException("package.version must be a safe version segment.");
            }
        }
    }

    private static void ValidateRuntime(RuntimeInfo runtime)
    {
        if (!PythonRuntimes.Contains(runtime.Python))
        {
            throw new InvalidDataException($"runtime.python '{runtime.Python}' is not supported.");
        }

        if (!NodeRuntimes.Contains(runtime.Node))
        {
            throw new InvalidDataException($"runtime.node '{runtime.Node}' is not supported.");
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

    private static void ValidateGitHubSegment(string value, string field)
    {
        if (!GitHubSegmentPattern.IsMatch(value) ||
            value.Equals(".", StringComparison.Ordinal) ||
            value.Equals("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{field} must be a safe GitHub owner or repository name.");
        }
    }

    private static void ValidateBranch(string value)
    {
        if (value == "*")
        {
            return;
        }

        if (value.Length > 244 ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.EndsWith("/", StringComparison.Ordinal) ||
            value.EndsWith(".", StringComparison.Ordinal) ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace) ||
            value.IndexOfAny(['\\', ':', '^', '~', '?', '*', '[']) >= 0)
        {
            throw new InvalidDataException("source.branch must be a safe git branch name.");
        }
    }

    private static void ValidateCommit(string value)
    {
        if (value == "*")
        {
            return;
        }

        if (!GitCommitPattern.IsMatch(value))
        {
            throw new InvalidDataException("source.commit must be a 7 to 40 character hexadecimal commit SHA or '*'.");
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
