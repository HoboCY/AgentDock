using System.Diagnostics;
using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

/// <summary>
/// Real pinned OpenCode pipeline tests. They run the in-sandbox runner directly
/// through the engine (the OS sandbox wrapper itself is validated separately by
/// <see cref="SandboxIntegrationTests"/>), so they cover the launch handshake,
/// isolated environment, JSONL events, usage, timing, cancellation and change
/// observation with no real credential and no remote request.
/// </summary>
public sealed class PipelineFactAttribute : FactAttribute
{
    public PipelineFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Windows runner fixtures; MacExecutionTests cover macOS."; return; }
        if (Environment.GetEnvironmentVariable("AGENTDOCK_TEST_OPENCODE") is null ||
            Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX") is null ||
            Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SHELL") is null)
        {
            Skip = "Set AGENTDOCK_TEST_OPENCODE, AGENTDOCK_TEST_SANDBOX and AGENTDOCK_TEST_SHELL to the pinned tools.";
        }
    }
}

public sealed class PipelineIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdock-pipeline-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _project;

    public PipelineIntegrationTests()
    {
        _home = Path.Combine(_root, "home");
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Bun may leave a locked native cache file behind; fixture cleanup is best effort.
        }
    }

    private DockConfiguration Settings() => new()
    {
        EnginePath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_OPENCODE")!,
        SandboxPath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX")!,
        ShellPath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SHELL")!,
    };

    private ExecutionTask TaskFor(string role, string prompt) =>
        new("pipeline-task", _project, role, prompt, Settings().Roles[role], Settings());

    private OpenCodeEngine Engine(ScriptedHandler handler, string? credential = null) =>
        new(_home, () => credential ?? "fixture-not-a-real-key", new EngineHooks
        {
            RelayTransport = handler,
            // Runs the real in-sandbox runner command that the sandbox would
            // launch, without the OS wrapper. This is a test seam only.
            ProcessStarter = RunSandboxRunner,
        });

    private static JobProcess RunSandboxRunner(string executable, IReadOnlyList<string> sandboxArguments,
        string cwd, IReadOnlyDictionary<string, string> environment)
    {
        var separator = sandboxArguments.ToList().IndexOf("--");
        var runner = sandboxArguments.Skip(separator + 1).ToArray();
        return JobProcess.Start(runner[0], runner.Skip(1).ToArray(), cwd, environment);
    }

    [PipelineFact]
    public async Task WriterToolCallProducesEventsUsageTimingAndObservedChanges()
    {
        var receipt = Path.Combine(_project, "receipt.txt");
        var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.ToolCall("write", new { filePath = receipt, content = "FIXTURE_RECEIPT\n" }),
            ScriptedModel.Text("FIXTURE_OK")));
        var events = new List<EngineEvent>();
        var result = await Engine(handler).ExecuteAsync(TaskFor("worker", "Write the receipt and report."),
            events.Add, CancellationToken.None);

        Assert.True(result.Success, result.Error + "\n" + string.Join("\n", events.Select(item => item.Text)));
        Assert.Equal("FIXTURE_RECEIPT\n", File.ReadAllText(receipt));
        Assert.Contains(events, item => item.Kind == "text" && item.Text.Contains("FIXTURE_OK", StringComparison.Ordinal));
        var usage = events.Last(item => item.Kind == "usage");
        Assert.True(usage.InputTokens is > 0, "input tokens should be reported");
        Assert.True(usage.OutputTokens is > 0, "output tokens should include reasoning");
        Assert.NotNull(usage.GenerationSeconds);
        Assert.Contains(events, item => item.Kind == "tool" && item.Text.Contains("completed", StringComparison.Ordinal));
        var change = Assert.Single(result.Changes!, item => item.Path == "receipt.txt");
        Assert.Null(change.BeforeSha256);
        Assert.NotNull(change.AfterSha256);
        Assert.Contains("not attribution", result.Result);
        Assert.True(handler.Requests >= 2, "the fake model should have served the tool round and the final round");
    }

    [PipelineFact]
    public async Task CredentialEchoIsRedactedFromEventsAndFinalResult()
    {
        const string credential = "sk-fixture-echo-0123456789";
        var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.Text("the credential is " + credential)));
        var events = new List<EngineEvent>();
        var result = await Engine(handler, credential).ExecuteAsync(
            TaskFor("worker", "Echo the fixture credential."), events.Add, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("[redacted]", result.Result);
        Assert.DoesNotContain(credential, result.Result);
        Assert.DoesNotContain(credential, string.Join("\n", events.Select(item => item.Text)));
    }

    [PipelineFact]
    public async Task ExplorerEditAttemptIsRefusedAndProjectStaysUnchanged()
    {
        var receipt = Path.Combine(_project, "receipt.txt");
        var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.ToolCall("write", new { filePath = receipt, content = "FORBIDDEN" }),
            ScriptedModel.Text("FIXTURE_OK")));
        var events = new List<EngineEvent>();
        var result = await Engine(handler).ExecuteAsync(TaskFor("explorer", "Attempt a write and report the result."),
            events.Add, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(receipt), "the engine permission profile must refuse an explorer edit");
        Assert.Empty(result.Changes ?? []);
    }

    [PipelineFact]
    public async Task CancellationKillsTheRealToolChildAndRetainsProjectEdits()
    {
        var pidFile = Path.Combine(_project, "child.pid");
        var script = $"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 90";
        var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = script, description = "Cancellation fixture", timeout = 120000 }),
            ScriptedModel.Text("FIXTURE_OK")));
        using var source = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline && !source.IsCancellationRequested)
            {
                await Task.Delay(200);
            }
            if (File.Exists(pidFile))
            {
                source.Cancel();
            }
        });
        var result = await Engine(handler).ExecuteAsync(TaskFor("worker", "Start the cancellation fixture."),
            _ => { }, source.Token);
        await watcher;

        Assert.False(result.Success);
        Assert.Contains("Cancelled", result.Error);
        Assert.True(File.Exists(pidFile), "the script process should have written its PID before cancellation");
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (IsRunning(pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }
        Assert.False(IsRunning(pid), "the real tool child survived cancellation");
        Assert.Contains(result.Changes!, change => change.Path == "child.pid");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
