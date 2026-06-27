using System.Text.Json;

namespace WebAppLauncher;

internal sealed class LauncherCommandDispatcher
{
    private readonly Dictionary<string, Func<LauncherCommand, Task>> handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private LauncherCommandDispatcher()
    {
    }

    public static LauncherCommand? Deserialize(string json, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<LauncherCommand>(json, options);
    }

    public static Builder CreateBuilder()
    {
        return new Builder(new LauncherCommandDispatcher());
    }

    public Task DispatchAsync(LauncherCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Type))
        {
            throw new InvalidOperationException("잘못된 런처 명령입니다.");
        }

        if (!handlers.TryGetValue(command.Type, out var handler))
        {
            throw new InvalidOperationException($"지원하지 않는 명령입니다: {command.Type}");
        }

        return handler(command);
    }

    public sealed class Builder
    {
        private readonly LauncherCommandDispatcher dispatcher;

        internal Builder(LauncherCommandDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
        }

        public Builder On(string type, Action<LauncherCommand> handler)
        {
            dispatcher.handlers.Add(type, command =>
            {
                handler(command);
                return Task.CompletedTask;
            });
            return this;
        }

        public Builder OnAsync(string type, Func<LauncherCommand, Task> handler)
        {
            dispatcher.handlers.Add(type, handler);
            return this;
        }

        public LauncherCommandDispatcher Build()
        {
            return dispatcher;
        }
    }
}

internal sealed record LauncherCommand(
    string Type,
    string? PackageId = null,
    string? Version = null,
    bool? Enabled = null,
    int? ProcessId = null);
