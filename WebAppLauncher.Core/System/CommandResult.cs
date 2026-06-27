namespace WebAppLauncher.Core;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
