using WebAppLauncher.Core;

var exitCode = await Cli.RunAsync(args);
return exitCode;

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            var root = GetOption(args, "--root");
            var paths = new WebAppPaths(root);
            paths.EnsureRootLayout();
            var repository = new AppRepository(paths);
            var installer = new AppInstaller(paths);

            switch (args[0].ToLowerInvariant())
            {
                case "install":
                    RequireArg(args, 1, "install requires a .wapk path.");
                    var app = await installer.InstallAsync(args[1]);
                    Console.WriteLine($"Installed {app.Manifest.Package.Id}/{app.Manifest.Package.Version}");
                    Console.WriteLine($"Origin: {app.Manifest.Network.Origin}");
                    Console.WriteLine($"Path: {app.InstallDirectory}");
                    return 0;

                case "list":
                    foreach (var installed in repository.ListInstalled())
                    {
                        Console.WriteLine($"{installed.Manifest.Package.Id}\t{installed.Manifest.Package.Version}\t{installed.Manifest.Network.Origin}\t{installed.InstallDirectory}");
                    }
                    return 0;

                case "run":
                    RequireArg(args, 1, "run requires package id.");
                    var runVersion = GetOption(args, "--version");
                    var runApp = repository.GetInstalled(args[1], runVersion);
                    var launch = new AppLauncher(paths).Launch(runApp);
                    Console.WriteLine($"Opened {launch.Uri}");
                    if (launch.Process is not null)
                    {
                        Console.WriteLine($"Backend PID: {launch.Process.Id}");
                        Console.WriteLine($"Log: {launch.LogPath}");
                    }
                    return 0;

                case "remove":
                    RequireArg(args, 1, "remove requires package id.");
                    installer.Remove(args[1], GetOption(args, "--version"));
                    Console.WriteLine($"Removed {args[1]}");
                    return 0;

                case "doctor":
                    Console.WriteLine($"Root: {paths.Root}");
                    foreach (var line in new ToolResolver(paths).Doctor())
                    {
                        Console.WriteLine(line);
                    }
                    return 0;

                case "port":
                    return HandlePort(args, installer, repository);

                default:
                    throw new InvalidOperationException($"Unknown command: {args[0]}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int HandlePort(string[] args, AppInstaller installer, AppRepository repository)
    {
        RequireArg(args, 1, "port requires a subcommand.");
        switch (args[1].ToLowerInvariant())
        {
            case "list":
                foreach (var app in repository.ListInstalled())
                {
                    Console.WriteLine($"{app.Manifest.Network.Port}\t{app.Manifest.Package.Id}\t{app.Manifest.Package.Version}");
                }
                return 0;

            case "reassign":
                RequireArg(args, 3, "port reassign requires package id and port.");
                if (!int.TryParse(args[3], out var port))
                {
                    throw new InvalidOperationException("port must be an integer.");
                }

                Console.Error.WriteLine("Warning: changing a port changes the browser origin. localStorage/IndexedDB/Cache Storage may no longer be visible.");
                installer.ReassignPort(args[2], port, GetOption(args, "--version"));
                Console.WriteLine($"Reassigned {args[2]} to {port}");
                return 0;

            default:
                throw new InvalidOperationException($"Unknown port subcommand: {args[1]}");
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void RequireArg(string[] args, int index, string message)
    {
        if (args.Length <= index)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        WebAppLauncher.Cli

        Commands:
          install <file.wapk> [--root <path>]
          list [--root <path>]
          run <owner@repo> [--version <version>] [--root <path>]
          remove <owner@repo> [--version <version>] [--root <path>]
          doctor [--root <path>]
          port list [--root <path>]
          port reassign <owner@repo> <52000-52999> [--version <version>] [--root <path>]
        """);
    }
}
