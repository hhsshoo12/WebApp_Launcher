namespace WebAppLauncher.Core;

public sealed class ToolResolver
{
    private readonly WebAppPaths paths;

    public ToolResolver(WebAppPaths paths)
    {
        this.paths = paths;
    }

    public string Git => Resolve(paths.GitExecutable, "git");
    public string Uv => Resolve(paths.UvExecutable, "uv");
    public string Pnpm => ResolvePnpm();

    public string ResolvePython(string runtime)
    {
        if (runtime.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This app does not declare a Python runtime.");
        }

        return Resolve(paths.GetPythonExecutable(runtime), "python");
    }

    public string ResolveNode(string runtime)
    {
        if (runtime.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This app does not declare a Node.js runtime.");
        }

        return Resolve(paths.GetNodeExecutable(runtime), "node");
    }

    public IReadOnlyList<string> Doctor()
    {
        return
        [
            Describe("git", paths.GitExecutable, "git"),
            Describe("uv", paths.UvExecutable, "uv"),
            Describe("pnpm", ResolvePnpm(), "pnpm"),
            Describe("python313", paths.GetPythonExecutable("python313"), "python"),
            Describe("python314", paths.GetPythonExecutable("python314"), "python"),
            Describe("nodejs-lts-22", paths.GetNodeExecutable("nodejs-lts-22"), "node"),
            Describe("nodejs-lts-24", paths.GetNodeExecutable("nodejs-lts-24"), "node")
        ];
    }

    private string ResolvePnpm()
    {
        var exe = Path.Combine(paths.Tools, "pnpm", "pnpm.exe");
        if (File.Exists(exe))
        {
            return exe;
        }

        if (File.Exists(paths.PnpmExecutable))
        {
            return paths.PnpmExecutable;
        }

        return "pnpm";
    }

    private static string Resolve(string bundledPath, string commandName)
    {
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        return commandName;
    }

    private static string Describe(string name, string bundledPath, string fallback)
    {
        var status = File.Exists(bundledPath) ? bundledPath : $"PATH fallback: {fallback}";
        return $"{name}: {status}";
    }
}
