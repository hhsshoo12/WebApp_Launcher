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
                    Console.WriteLine("Port: assigned dynamically at launch");
                    Console.WriteLine($"Path: {app.InstallDirectory}");
                    return 0;

                case "list":
                    foreach (var installed in repository.ListInstalled())
                    {
                        Console.WriteLine($"{installed.Manifest.Package.Id}\t{installed.Manifest.Package.Version}\tdynamic\t{installed.InstallDirectory}");
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

                case "update":
                    RequireArg(args, 1, "update requires check, --all, or a package id.");
                    var updates = new AppUpdateManager(paths);
                    if (args[1].Equals("check", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var installed in repository.ListInstalled())
                        {
                            var status = await updates.CheckAsync(installed);
                            Console.WriteLine(
                                $"{status.PackageId}\t{status.InstalledVersion}\t" +
                                $"{status.LatestVersion ?? "-"}\t{status.Status}");
                        }

                        return 0;
                    }

                    var updateTargets = args[1].Equals("--all", StringComparison.OrdinalIgnoreCase)
                        ? repository.ListInstalled()
                        : [repository.GetInstalled(args[1], GetOption(args, "--version"))];
                    foreach (var target in updateTargets)
                    {
                        var prepared = await updates.PrepareAsync(target);
                        if (prepared is null)
                        {
                            Console.WriteLine($"Current {target.Manifest.Package.Id}/{target.Manifest.Package.Version}");
                            continue;
                        }

                        var applied = updates.Apply(target, prepared);
                        Console.WriteLine(
                            $"Updated {applied.Manifest.Package.Id} to {applied.Manifest.Package.Version}");
                    }

                    return 0;

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
          update check [--root <path>]
          update <owner@repo> [--version <version>] [--root <path>]
          update --all [--root <path>]
          doctor [--root <path>]
        """);
    }
}
