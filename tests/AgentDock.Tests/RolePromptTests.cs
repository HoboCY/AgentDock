using System.Text.Json;
using System.Threading.Channels;
using AgentDock.Core;
using Xunit;
using TaskStatus = AgentDock.Core.TaskStatus;

namespace AgentDock.Tests;

public sealed class RolePromptTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-role-prompts-" + Guid.NewGuid().ToString("N"));
    public RolePromptTests() => Directory.CreateDirectory(root);

    private static string Legacy(string role) =>
        $"You are the {role} external agent. Complete only the assigned work. Never delegate. Report evidence and remaining limitations.";

    private static DockConfiguration LegacyConfiguration() => new()
    {
        Roles = Roles.All.ToDictionary(role => role, role => new RoleConfiguration(Legacy(role), 321))
    };

    [Fact]
    public void FreshConfigurationHasDistinctEnglishRolesWithCompletionAndReportingInstructions()
    {
        var configuration = new DockConfiguration();
        Assert.Equal(5, configuration.Roles.Values.Select(role => role.Prompt).Distinct().Count());
        foreach (var role in Roles.All)
        {
            var prompt = configuration.Roles[role].Prompt;
            Assert.All(prompt, character => Assert.InRange((int)character, 0, 127));
            Assert.Contains("Workflow:", prompt);
            Assert.Contains("Completion:", prompt);
            Assert.Contains("requested response language and format", prompt);
            Assert.NotEqual(Legacy(role), prompt);
            Assert.Equal(1200, configuration.Roles[role].TimeoutSeconds);
        }
    }

    [Fact]
    public void MigrationOnlyReplacesExactOldTextAndPreservesTimeoutsAndOtherSettings()
    {
        var original = LegacyConfiguration() with { MaxConcurrency = 7, NotifyOnCompletion = true, OverlayOpacity = .5, EnginePath = "configured-engine" };
        original.Roles["reviewer"] = new(Legacy("reviewer") + " ", 77);
        original.Roles["verifier"] = new("My custom verification instructions", 88);
        var upgraded = RolePrompts.UpgradeLegacyDefaults(original);

        foreach (var role in new[] { "default", "explorer", "worker" })
        {
            Assert.Equal(new DockConfiguration().Roles[role].Prompt, upgraded.Roles[role].Prompt);
            Assert.Equal(321, upgraded.Roles[role].TimeoutSeconds);
            Assert.Equal(Legacy(role), original.Roles[role].Prompt);
        }
        Assert.Equal(original.Roles["reviewer"], upgraded.Roles["reviewer"]);
        Assert.Equal(original.Roles["verifier"], upgraded.Roles["verifier"]);
        Assert.Equal(original with { Roles = upgraded.Roles }, upgraded);
        Assert.Same(upgraded, RolePrompts.UpgradeLegacyDefaults(upgraded));
    }

    [Fact]
    public async Task StartupPersistsUpgradeButKeepsAcceptedTaskSnapshotsAndResumedTaskPrompt()
    {
        var file = Path.Combine(root, "tasks.json");
        var legacy = LegacyConfiguration();
        var state = new TaskService.ServiceState { Configuration = legacy };
        foreach (var status in new[] { TaskStatus.Queued, TaskStatus.Running, TaskStatus.Paused, TaskStatus.Completed })
        {
            var id = "old-" + status;
            state.Tasks[id] = new()
            {
                Id = id, RequestId = id, Project = root, Role = "worker", Title = "Previously accepted",
                Prompt = "Return the assigned result", Status = status, AcceptedAt = DateTimeOffset.UtcNow,
                Configuration = legacy.Roles["worker"], Settings = legacy
            };
            state.Requests[id] = new() { Id = id, Status = status, Fingerprint = "old-fixture" };
        }
        AtomicJson.Write(file, state);
        var engine = new RecordingEngine();
        await using (var service = new TaskService(root, engine))
        {
            Assert.Equal(RolePrompts.For("worker"), service.Configuration.Roles["worker"].Prompt);
            var persisted = JsonSerializer.Deserialize<TaskService.ServiceState>(File.ReadAllBytes(file), JsonDefaults.Options)!;
            Assert.Equal(service.Configuration.Roles["worker"], persisted.Configuration.Roles["worker"]);
            foreach (var task in persisted.Tasks.Values)
            {
                Assert.Equal(legacy.Roles["worker"], task.Configuration);
                Assert.Equal(legacy.Roles["worker"], task.Settings.Roles["worker"]);
            }
            Assert.False(engine.Captured.Reader.TryRead(out _));

            var fresh = service.Start(new("fresh-role-request", root, "New task", "worker", "Return the assigned result"));
            var freshExecution = await engine.Captured.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(fresh.Id, freshExecution.Id);
            Assert.Equal(RolePrompts.For("worker"), freshExecution.Configuration.Prompt);
            Assert.Equal(321, freshExecution.Configuration.TimeoutSeconds);

            service.Resume("old-Queued");
            var resumed = await engine.Captured.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("old-Queued", resumed.Id);
            Assert.Equal(legacy.Roles["worker"], resumed.Configuration);
        }
        var first = File.ReadAllBytes(file);
        await using (var reopened = new TaskService(root, new RecordingEngine()))
            Assert.Equal(RolePrompts.For("worker"), reopened.Configuration.Roles["worker"].Prompt);
        Assert.Equal(first, File.ReadAllBytes(file));
    }

    private sealed class RecordingEngine : IExecutionEngine
    {
        public Channel<ExecutionTask> Captured { get; } = Channel.CreateUnbounded<ExecutionTask>();
        public Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report, CancellationToken cancellationToken)
        {
            Captured.Writer.TryWrite(task);
            return Task.FromResult(new ExecutionResult(true, "Verified fixture"));
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
