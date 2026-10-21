using AgentDock.Core;
using Xunit;
using TaskStatus = AgentDock.Core.TaskStatus;

namespace AgentDock.Tests;

public sealed class ConfigurationAndBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-tests-" + Guid.NewGuid().ToString("N"));
    public ConfigurationAndBoundaryTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task DefaultsAreFourSlotsFiveRolesAndNoCompletionOrLoginNotifications()
    {
        await using var service = new TaskService(Path.Combine(root, "state"), new BlockingEngine());
        var config = service.Configuration;
        Assert.Equal(4, config.MaxConcurrency);
        Assert.Equal(new[] { "default", "explorer", "reviewer", "verifier", "worker" }, config.Roles.Keys.Order());
        Assert.False(config.StartAtLogin);
        Assert.False(config.NotifyOnCompletion);
        config.Roles["architect"] = new("architect");
        Assert.Equal("InvalidConfiguration", Assert.Throws<DockException>(() => service.Configure(config)).Code);
        Assert.DoesNotContain("architect", service.Configuration.Roles.Keys);
    }

    [Fact]
    public async Task TimeoutReleasesItsSlotAndKeepsTheOriginalRequestTerminal()
    {
        await using var service = new TaskService(Path.Combine(root, "state"), new BlockingEngine());
        var config = service.Configuration;
        config.Roles["worker"] = new("worker", 1);
        service.Configure(config);
        var request = new TaskRequest("timeout-request", root, "timeout", "worker", "task");
        var task = service.Start(request);
        var detail = service.Get(task.Id);
        while (detail.Summary.Status.IsUnfinished()) detail = await service.WaitAsync(task.Id, detail.NextCursor, TimeSpan.FromSeconds(5));
        Assert.Equal(TaskStatus.TimedOut, detail.Summary.Status);
        Assert.Equal(TaskStatus.TimedOut, service.Start(request).Status);
    }

    [Fact]
    public async Task CleanupNeverRemovesQueuedOrRunningTasks()
    {
        await using var service = new TaskService(Path.Combine(root, "state"), new BlockingEngine());
        service.Start(new("cleanup-first", root, "one", "worker", "task"));
        service.Start(new("cleanup-second", root, "two", "worker", "task"));
        var preview = service.Cleanup(false, "all");
        Assert.Equal(0, preview.Summaries);
        Assert.Equal(0, preview.Bodies);
        Assert.Equal(2, preview.UnfinishedProtected);
        Assert.NotNull(preview.Revision);
        service.Cleanup(true, "all");
        Assert.Equal(2, service.List().Length);
    }

    [Fact]
    public async Task SecondOwnerCannotStartAndCorruptStateIsNeverReplaced()
    {
        var home = Path.Combine(root, "state");
        await using (var service = new TaskService(home, new BlockingEngine()))
            Assert.Throws<IOException>(() => new TaskService(home, new BlockingEngine()));
        var file = Path.Combine(home, "tasks.json");
        File.WriteAllText(file, "invalid state");
        Assert.Throws<System.Text.Json.JsonException>(() => new TaskService(home, new BlockingEngine()));
        Assert.Equal("invalid state", File.ReadAllText(file));
    }

    [WindowsOwnershipFact]
    public async Task StartupWaitsForBriefWindowsOwnershipConflict()
    {
        var home = Path.Combine(root, "brief-lock");
        Directory.CreateDirectory(home);
        using var held = new FileStream(Path.Combine(home, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var release = new ManualResetEventSlim();
        var releaser = new Thread(() => { release.Wait(); Thread.Sleep(100); held.Dispose(); });
        releaser.Start();
        try
        {
            release.Set();
            await using var service = new TaskService(home, new BlockingEngine());
            Assert.Empty(service.List());
            Assert.True(File.Exists(Path.Combine(home, "tasks.json")));
        }
        finally { release.Set(); releaser.Join(); }
    }

    [Fact]
    public void InvalidOwnershipPathDoesNotCreateTaskState()
    {
        var home = Path.Combine(root, "invalid-lock");
        Directory.CreateDirectory(Path.Combine(home, "owner.lock"));
        Assert.Throws<UnauthorizedAccessException>(() => new TaskService(home, new BlockingEngine()));
        Assert.False(File.Exists(Path.Combine(home, "tasks.json")));
    }

    [Fact]
    public void PathComparisonIncludesParentChildButNotPrefixSiblings()
    {
        Assert.True(ProjectPaths.Overlap(Path.Combine(root, "project"), Path.Combine(root, "project", "src")));
        Assert.False(ProjectPaths.Overlap(Path.Combine(root, "project"), Path.Combine(root, "project-other")));
        Assert.Equal("InvalidProject", Assert.Throws<DockException>(() => ProjectPaths.Normalize("relative")).Code);
    }

    [MacPathFact]
    public async Task MacPathAliasesCannotStartOverlappingWriters()
    {
        var project = Path.Combine(root, "CaseProject");
        Directory.CreateDirectory(project);
        var differentCase = Path.Combine(root, "caseproject");
        var alias = Path.Combine(root, "project-link");
        Directory.CreateSymbolicLink(alias, project);
        await using var service = new TaskService(Path.Combine(root, "state"), new BlockingEngine());
        var first = service.Start(new("mac-canonical", project, "one", "worker", "task"));
        var second = service.Start(new("mac-alias", alias, "two", "worker", "task"));
        Assert.Equal(first.Project, second.Project);
        Assert.Equal(TaskStatus.Queued, second.Status);
        if (Directory.Exists(differentCase))
        {
            var third = service.Start(new("mac-case", differentCase, "three", "worker", "task"));
            Assert.Equal(first.Project, third.Project);
            Assert.Equal(TaskStatus.Queued, third.Status);
        }
        else
        {
            Directory.CreateDirectory(differentCase);
            Assert.NotEqual(ProjectPaths.Normalize(project), ProjectPaths.Normalize(differentCase));
        }
    }

    private sealed class BlockingEngine : IExecutionEngine
    {
        public async Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(true, "");
        }
    }
    public void Dispose() => Directory.Delete(root, true);
}

public sealed class MacPathFactAttribute : FactAttribute
{
    public MacPathFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires real macOS path identity semantics.";
    }
}

public sealed class WindowsOwnershipFactAttribute : FactAttribute
{
    public WindowsOwnershipFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows sharing-violation semantics.";
    }
}
