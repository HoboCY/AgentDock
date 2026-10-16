using AgentDock.Core;

namespace AgentDock.Engine.Tests;

public static class MacCrashFixture
{
    public static async Task Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || args.Length != 2 || args[0] != "--mac-crash-fixture") return;
        var root = ProjectPaths.Canonicalize(args[1]);
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var settings = new DockConfiguration
        {
            EnginePath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_OPENCODE")!,
            SandboxPath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX")!,
            ShellPath = "/bin/bash",
        };
        var task = new ExecutionTask("crash-fixture", project, "worker", "Run the fixture.", settings.Roles["worker"], settings);
        using var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = MacExecutionTests.DetachedCommand(project, true), description = "Start bounded detached fixture", timeout = 120000 }),
            ScriptedModel.Text("UNEXPECTED_COMPLETION")));
        var engine = new OpenCodeEngine(root, () => "synthetic-key-not-real", new EngineHooks { RelayTransport = handler });
        await engine.ExecuteAsync(task, _ => { }, CancellationToken.None);
    }
}
