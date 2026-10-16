using AgentDock.Core;

namespace AgentDock.Tests;

public static class RecoveryFixture
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--pipe-auth-probe")
        {
            Console.WriteLine("PROBE_STARTED");
            Console.WriteLine(File.ReadAllText(Path.Combine(args[1], "connection.token")).Length == 64 ? "TOKEN_READ" : "TOKEN_MISSING");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await new DockClient(args[1]).InvokeAsync<HostStatus>("status", cancellationToken: timeout.Token); Console.WriteLine("ACCEPTED"); }
            catch (DockException error) { Console.WriteLine(error.Code); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
            { Console.WriteLine("PIPE_DENIED:" + error.GetType().Name); }
            return;
        }
        if (args.Length != 2 || args[0] != "--crash-fixture") return;
        await using var service = new TaskService(Path.Combine(args[1], "state"), new NeverEndingEngine());
        service.Configure(service.Configuration with { MaxConcurrency = 1 });
        var running = service.Start(new("crash-running", args[1], "running", "worker", "work"));
        var queued = service.Start(new("crash-queued", args[1], "queued", "worker", "work"));
        Console.WriteLine(running.Id + "," + queued.Id);
        await Task.Delay(Timeout.Infinite);
    }
    private sealed class NeverEndingEngine : IExecutionEngine
    {
        public async Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(true, "");
        }
    }
}
