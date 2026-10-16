using AgentDock.Core;
using Xunit;
using TaskStatus = AgentDock.Core.TaskStatus;

namespace AgentDock.Tests;

public sealed class TaskLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-tests-" + Guid.NewGuid().ToString("N"));
    public TaskLifecycleTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task RepeatedRequestsReturnTheOriginalTaskAndRejectChangedArguments()
    {
        var engine = new HeldEngine();
        await using var service = new TaskService(Path.Combine(root, "state"), engine);
        var request = new TaskRequest("request-0001", root, "Inspect", "explorer", "Read the project");
        var first = service.Start(request);
        var retry = service.Start(request);
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal("RequestConflict", Assert.Throws<DockException>(() => service.Start(request with { Prompt = "Different" })).Code);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.Finish.TrySetResult(new(true, "done"));
        var completed = await service.WaitAsync(first.Id, 0, TimeSpan.FromSeconds(5));
        while (completed.Summary.Status.IsUnfinished())
            completed = await service.WaitAsync(first.Id, completed.NextCursor, TimeSpan.FromSeconds(5));
        Assert.Equal(TaskStatus.Completed, completed.Summary.Status);
        Assert.Equal(first.Id, service.Start(request).Id);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class HeldEngine : IExecutionEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ExecutionResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Finish.Task.WaitAsync(cancellationToken);
        }
    }
}
