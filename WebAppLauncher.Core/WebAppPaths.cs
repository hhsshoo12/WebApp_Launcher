namespace WebAppLauncher.Core;

public sealed class WebAppPaths
{
    public WebAppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".webapp");
    }

    public string Root { get; }
    public string Runtime => Path.Combine(Root, "runtime");
    public string Tools => Path.Combine(Root, "tools");
    public string Packages => Path.Combine(Root, "packages");
    public string Apps => Path.Combine(Root, "app");
    public string GitCache => Path.Combine(Packages, "git-cache");
    public string UvCache => Path.Combine(Packages, "uv-cache");
    public string PnpmStore => Path.Combine(Packages, "pnpm-store");

    public string GitExecutable => Path.Combine(Tools, "git", "cmd", "git.exe");
    public string UvExecutable => Path.Combine(Tools, "uv", "uv.exe");
    public string PnpmExecutable => Path.Combine(Tools, "pnpm", "pnpm.cmd");

    public string GetPythonExecutable(string runtime)
    {
        return Path.Combine(Runtime, runtime, "python.exe");
    }

    public string GetNodeExecutable(string runtime)
    {
        return Path.Combine(Runtime, runtime, "node.exe");
    }

    public string GetAppDirectory(string packageId, string version)
    {
        return Path.Combine(Apps, SanitizeSegment(packageId), SanitizeSegment(version));
    }

    public void EnsureRootLayout()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(Tools);
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Apps);
        Directory.CreateDirectory(GitCache);
        Directory.CreateDirectory(UvCache);
        Directory.CreateDirectory(PnpmStore);

        Directory.CreateDirectory(Path.Combine(Runtime, "python313"));
        Directory.CreateDirectory(Path.Combine(Runtime, "python314"));
        Directory.CreateDirectory(Path.Combine(Runtime, "nodejs-lts-22"));
        Directory.CreateDirectory(Path.Combine(Runtime, "nodejs-lts-24"));
        Directory.CreateDirectory(Path.Combine(Tools, "pnpm"));
        Directory.CreateDirectory(Path.Combine(Tools, "uv"));
        Directory.CreateDirectory(Path.Combine(Tools, "git"));
    }

    public static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
