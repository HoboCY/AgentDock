using System.Collections.Concurrent;
using System.Diagnostics;
using AgentDock.Core;
using Xunit;
using TaskStatus = AgentDock.Core.TaskStatus;

namespace AgentDock.Tests;

public sealed class SchedulingAndRetentionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-tests-" + Guid.NewGuid().ToString("N"));
    public SchedulingAndRetentionTests() => Directory.CreateDirectory(root);
    private string Project(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private TaskRequest Request(string key, string project, string role = "worker") => new("request-" + key, project, key, role, "assigned task");

    [Fact]
    public async Task QueueUsesFreeSlotsAcrossProjectsButSerializesOverlappingWrites()
    {
        var engine = new ControlledEngine();
        await using var service = new TaskService(Path.Combine(root, "state"), engine);
        service.Configure(service.Configuration with { MaxConcurrency = 2 });
        var first = service.Start(Request("parent", Project("a")));
        var child = service.Start(Request("child", Project("a/sub")));
        var other = service.Start(Request("other", Project("b")));
        var extra = service.Start(Request("extra", Project("c")));
        Assert.Equal(TaskStatus.Queued, service.Get(child.Id).Summary.Status);
        Assert.Equal(TaskStatus.Running, service.Get(other.Id).Summary.Status);
        Assert.Equal(TaskStatus.Queued, service.Get(extra.Id).Summary.Status);
        await engine.Complete(first.Id);
        await Until(() => service.Get(child.Id).Summary.Status == TaskStatus.Running);
        Assert.Equal(TaskStatus.Queued, service.Get(extra.Id).Summary.Status);
    }

    [Fact]
    public async Task QueuedTaskKeepsAcceptedRoleAndTimeoutAfterGlobalConfigurationChanges()
    {
        var engine = new ControlledEngine();
        await using var service = new TaskService(Path.Combine(root, "state"), engine);
        service.Configure(service.Configuration with { MaxConcurrency = 1 });
        var initial = service.Configuration.Roles["worker"];
        var first = service.Start(Request("first", root));
        var second = service.Start(Request("second", root));
        var config = service.Configuration;
        config.Roles["worker"] = new("new role prompt", 99);
        service.Configure(config);
        await engine.Complete(first.Id);
        await Until(() => engine.Tasks.ContainsKey(second.Id));
        Assert.Equal(initial, engine.Tasks[second.Id].Configuration);
        var third = service.Start(Request("third", root));
        await engine.Complete(second.Id);
        await Until(() => engine.Tasks.ContainsKey(third.Id));
        Assert.Equal("new role prompt", engine.Tasks[third.Id].Configuration.Prompt);
    }

    [Fact]
    public async Task UsageReplaysAndPartialRevisionsAreNotDoubleCountedAndUnknownIsNotZero()
    {
        var engine = new ControlledEngine();
        await using var service = new TaskService(Path.Combine(root, "state"), engine);
        var task = service.Start(Request("usage", root));
        Assert.Null(task.InputTokens);
        await Until(() => engine.Reporters.ContainsKey(task.Id));
        var report = engine.Reporters[task.Id];
        report(new("usage", UsageId: "turn-1", InputTokens: 100, OutputTokens: 40, GenerationSeconds: 2));
        report(new("usage", UsageId: "turn-1", InputTokens: 100, OutputTokens: 40, GenerationSeconds: 2));
        report(new("usage", UsageId: "turn-1", OutputTokens: 50, GenerationSeconds: 2));
        report(new("usage", UsageId: "turn-2", InputTokens: 30, OutputTokens: 10, GenerationSeconds: 1));
        var summary = service.Get(task.Id).Summary;
        Assert.Equal(130, summary.InputTokens);
        Assert.Equal(60, summary.OutputTokens);
        Assert.Equal(10, summary.GenerationTokensPerSecond);
        report(new("usage", UsageId: "turn-3", InputTokens: 7));
        Assert.Equal(137, service.Get(task.Id).Summary.InputTokens);
        Assert.Null(service.Get(task.Id).Summary.OutputTokens);
        report(new("usage", UsageId: "turn-3", OutputTokens: 5));
        Assert.Equal(65, service.Get(task.Id).Summary.OutputTokens);
    }

    [Fact]
    public async Task CleanupRequiresFreshPreviewWhenAnotherTaskFinishes()
    {
        var engine = new ControlledEngine();
        await using var service = new TaskService(Path.Combine(root, "state"), engine);
        var first = service.Start(Request("cleanup-first", root));
        await engine.Complete(first.Id);
        await Until(() => !service.Get(first.Id).Summary.Status.IsUnfinished());
        var preview = service.Cleanup(false, "all");
        var next = service.Start(Request("cleanup-next", root));
        await engine.Complete(next.Id);
        await Until(() => !service.Get(next.Id).Summary.Status.IsUnfinished());
        Assert.Equal("CleanupChanged", Assert.Throws<DockException>(() => service.Cleanup(true, "all", expectedRevision: preview.Revision)).Code);
        Assert.Equal(2, service.List().Length);
        var fresh = service.Cleanup(false, "all");
        Assert.Equal(2, service.Cleanup(true, "all", expectedRevision: fresh.Revision).Summaries);
    }

    [Fact]
    public async Task RetentionUsesFinishTimeAndRetainsIdentityAfterBodyAndSummaryDeletion()
    {
        var time = new ManualTime();
        var engine = new ControlledEngine();
        var request = Request("retention", root);
        var home = Path.Combine(root, "state");
        string id;
        await using (var service = new TaskService(home, engine, time))
        {
            id = service.Start(request).Id;
            time.Advance(TimeSpan.FromDays(100));
            Assert.Equal(0, service.Cleanup(true).Bodies);
            await engine.Complete(id);
            await Until(() => service.Get(id).Summary.Status == TaskStatus.Completed);
            time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromTicks(1));
            Assert.Equal(0, service.Cleanup(true).Bodies);
            time.Advance(TimeSpan.FromTicks(1));
            Assert.Equal(1, service.Cleanup(true).Bodies);
            Assert.Equal("expired", service.Get(id).BodyState);
            time.Advance(TimeSpan.FromDays(23));
            Assert.Equal(1, service.Cleanup(true).Summaries);
            Assert.Equal(id, service.Start(request).Id);
            Assert.Equal(TaskStatus.Completed, service.Start(request).Status);
        }
        await using var reopened = new TaskService(home, engine, time);
        Assert.Equal(id, reopened.Start(request).Id);
        Assert.Empty(reopened.List());
        Assert.Equal("SummaryExpired", Assert.Throws<DockException>(() => reopened.Get(id)).Code);
    }

    [Fact]
    public async Task ConfirmedExitCancelsUnfinishedTasksAndNeverRollsBackFiles()
    {
        var engine = new ControlledEngine();
        await using var service = new TaskService(Path.Combine(root, "state"), engine);
        service.Configure(service.Configuration with { MaxConcurrency = 1 });
        var first = service.Start(Request("running", root));
        var second = service.Start(Request("queued", root));
        var file = Path.Combine(root, "kept.txt"); File.WriteAllText(file, "keep this edit");
        Assert.Equal("ConfirmationRequired", (await Assert.ThrowsAsync<DockException>(() => service.ShutdownAsync(false))).Code);
        await service.ShutdownAsync(true);
        Assert.Equal(TaskStatus.Cancelled, service.Get(first.Id).Summary.Status);
        Assert.Equal(TaskStatus.Cancelled, service.Get(second.Id).Summary.Status);
        Assert.Equal("keep this edit", File.ReadAllText(file));
    }

    [Fact]
    public async Task ProcessCrashInterruptsRunningTasksAndPausesQueueUntilExplicitResume()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(RecoveryFixture).Assembly.Location);
        start.ArgumentList.Add("--crash-fixture"); start.ArgumentList.Add(root);
        using var process = Process.Start(start)!;
        try
        {
            var ids = (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!.Split(',');
            process.Kill(true); await process.WaitForExitAsync();
            var engine = new ControlledEngine();
            await using var service = new TaskService(Path.Combine(root, "state"), engine);
            Assert.Equal(TaskStatus.Interrupted, service.Get(ids[0]).Summary.Status);
            Assert.Equal(TaskStatus.Paused, service.Get(ids[1]).Summary.Status);
            Assert.Empty(engine.Tasks);
            Assert.Equal("NotPaused", Assert.Throws<DockException>(() => service.Resume(ids[0])).Code);
            Assert.Equal(TaskStatus.Running, service.Resume(ids[1]).Status);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task ConcurrentRetriesProduceOneDurableTask()
    {
        var engine = new ControlledEngine();
        await using var service = new TaskService(Path.Combine(root, "state"), engine);
        var request = Request("parallel", root);
        var summaries = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.Start(request))));
        Assert.Single(summaries.Select(x => x.Id).Distinct());
        Assert.Single(service.List());
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
    private sealed class ControlledEngine : IExecutionEngine
    {
        public ConcurrentDictionary<string, ExecutionTask> Tasks { get; } = new();
        public ConcurrentDictionary<string, Action<EngineEvent>> Reporters { get; } = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ExecutionResult>> completions = new();
        public Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report, CancellationToken cancellationToken)
        {
            Tasks[task.Id] = task;
            Reporters[task.Id] = report;
            var source = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            completions[task.Id] = source;
            return source.Task.WaitAsync(cancellationToken);
        }
        public async Task Complete(string id)
        {
            await Until(() => completions.ContainsKey(id));
            completions[id].TrySetResult(new(true, "finished"));
        }
    }
}
