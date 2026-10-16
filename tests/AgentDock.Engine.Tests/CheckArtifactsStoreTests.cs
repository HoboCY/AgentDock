using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class CheckArtifactsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdock-artifacts-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public CheckArtifactsStoreTests()
    {
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Fixture cleanup is best effort.
        }
    }

    private string JobArtifacts(string taskId) => Path.Combine(_home, "engine", taskId, "runtime", "check-artifacts");

    [Fact]
    public void RestartRecoversCheckEvidenceAndRemovesAbandonedScratchOnly()
    {
        var abandoned = Path.Combine(_home, "engine", "task-1-abc123de");
        var evidence = Path.Combine(abandoned, "runtime", "check-artifacts");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "results.trx"), "partial evidence");
        File.WriteAllText(Path.Combine(abandoned, "temporary-config.json"), "temporary");
        var unrelated = Path.Combine(_home, "engine", "unrecognized");
        Directory.CreateDirectory(unrelated);
        OpenCodeEngine.RecoverAbandonedJobs(_home);
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(unrelated));
        Assert.Equal("partial evidence", File.ReadAllText(Path.Combine(_home, "artifacts", "task-1", "results.trx")));
    }

    [Fact]
    public void BodyCleanupRemovesOnlyArtifactsWhoseTaskBodiesAreNoLongerAvailable()
    {
        foreach (var id in new[] { "keep", "expired" })
        {
            var source = JobArtifacts(id);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "result.txt"), id);
            CheckArtifactsStore.Retain(_home, id, source);
        }
        OpenCodeEngine.CleanupArtifacts(_home, ["keep"]);
        Assert.True(File.Exists(Path.Combine(_home, "artifacts", "keep", "result.txt")));
        Assert.False(Directory.Exists(Path.Combine(_home, "artifacts", "expired")));
    }

    [Fact]
    public void RetainMovesCheckArtifactsUnderTheHomeRoot()
    {
        var source = JobArtifacts("task-1");
        Directory.CreateDirectory(Path.Combine(source, "test-results"));
        File.WriteAllText(Path.Combine(source, "test-results", "results.trx"), "fixture");

        var target = CheckArtifactsStore.Retain(_home, "task-1", source);

        Assert.Equal(Path.Combine(_home, "artifacts", "task-1"), target);
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(target!, "test-results", "results.trx")));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void EmptyOrMissingArtifactsAreNotRetained()
    {
        var empty = JobArtifacts("task-empty");
        Directory.CreateDirectory(empty);

        Assert.Null(CheckArtifactsStore.Retain(_home, "task-empty", empty));
        Assert.Null(CheckArtifactsStore.Retain(_home, "task-missing", JobArtifacts("task-missing")));
        Assert.False(Directory.Exists(Path.Combine(_home, "artifacts")));
    }

    [Fact]
    public void OversizedCheckArtifactsAreDiscardedAndLeftForJobCleanup()
    {
        var source = JobArtifacts("task-big");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "dump.bin"), new byte[256]);

        Assert.Null(CheckArtifactsStore.Retain(_home, "task-big", source, maxTaskBytes: 64));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public void OldestArtifactDirectoriesArePrunedBeyondTheBound()
    {
        var artifacts = Path.Combine(_home, "artifacts");
        for (var index = 0; index < CheckArtifactsStore.MaxTasks; index++)
        {
            var directory = Path.Combine(artifacts, "old-" + index);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "file.txt"), "x");
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(index - CheckArtifactsStore.MaxTasks - 1));
        }
        var source = JobArtifacts("newest");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "x");

        CheckArtifactsStore.Retain(_home, "newest", source);

        var remaining = Directory.GetDirectories(artifacts);
        Assert.Equal(CheckArtifactsStore.MaxTasks, remaining.Length);
        Assert.Contains(remaining, path => path.EndsWith("newest", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(remaining, path => path.EndsWith("old-0", StringComparison.OrdinalIgnoreCase));
    }
}
