using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

/// <summary>
/// Integration through the real Core <see cref="TaskService"/>: the adapter
/// emits one usage contribution per step_finish part, the service sums exactly
/// one contribution per UsageId, and replayed or revised parts never double
/// count two turns of known totals.
/// </summary>
public sealed class UsageIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdock-usage-" + Guid.NewGuid().ToString("N"));

    public UsageIntegrationTests()
    {
        Directory.CreateDirectory(_root);
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

    private async Task<TaskSummary> RunAsync(IExecutionEngine engine)
    {
        await using var service = new TaskService(_root, engine);
        var summary = service.Start(new TaskRequest("request-usage-0001", _root, "Usage integration", "worker", "Do work."));
        var detail = await service.WaitAsync(summary.Id, long.MaxValue, TimeSpan.FromSeconds(30));
        Assert.False(detail.Summary.Status.IsUnfinished(), detail.Summary.Error);
        return detail.Summary;
    }

    [Fact]
    public async Task TwoTurnsProduceKnownTotalsWithoutDoubleCounting()
    {
        var engine = new AdapterEngine(
            Fixtures.StepFinish("tool-calls", id: "prt_turn1", tokensJson: Fixtures.Tokens(10, 4, 1, 2, 3)),
            Fixtures.StepFinish("stop", id: "prt_turn2", tokensJson: Fixtures.Tokens(5, 2, 1, 1, 0)));

        var summary = await RunAsync(engine);

        // Turn 1: input 10+2+3 = 15, output 4+1 = 5.
        // Turn 2: input 5+1+0 = 6, output 2+1 = 3. Cumulative totals must not appear.
        Assert.Equal(21, summary.InputTokens);
        Assert.Equal(8, summary.OutputTokens);
    }

    [Fact]
    public async Task ReplayedAndRevisedUsagePartsNeverDoubleCount()
    {
        var engine = new ReplayEngine();
        var summary = await RunAsync(engine);

        // The exact replay is ignored and the revision for the same UsageId
        // replaces the first contribution (100/50), then the second turn adds
        // 6/3: a double count would have produced 15+100+6 or more.
        Assert.Equal(106, summary.InputTokens);
        Assert.Equal(53, summary.OutputTokens);
    }

    /// <summary>Reports adapter events for the given pinned JSONL lines.</summary>
    private sealed class AdapterEngine(params string[] lines) : IExecutionEngine
    {
        public Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report,
            CancellationToken cancellationToken)
        {
            var adapter = new OpenCodeEventAdapter();
            foreach (var line in lines)
            {
                foreach (var engineEvent in adapter.Feed(line))
                {
                    report(engineEvent);
                }
            }
            return Task.FromResult(new ExecutionResult(true, "fixture"));
        }
    }

    /// <summary>
    /// Emits a first turn, then re-delivers the same usage event and a revised
    /// version for the same UsageId, then a second turn.
    /// </summary>
    private sealed class ReplayEngine : IExecutionEngine
    {
        public Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report,
            CancellationToken cancellationToken)
        {
            var adapter = new OpenCodeEventAdapter();
            var first = adapter.Feed(Fixtures.StepFinish("tool-calls", id: "prt_replay",
                tokensJson: Fixtures.Tokens(10, 4, 1, 2, 3)));
            var original = Assert.Single(first);
            report(original);
            report(original);
            foreach (var revision in adapter.Feed(Fixtures.StepFinish("tool-calls", id: "prt_replay",
                tokensJson: Fixtures.Tokens(100, 50, 0, 0, 0)))) report(revision);
            foreach (var engineEvent in adapter.Feed(Fixtures.StepFinish("stop", id: "prt_final",
                tokensJson: Fixtures.Tokens(5, 2, 1, 1, 0))))
            {
                report(engineEvent);
            }
            return Task.FromResult(new ExecutionResult(true, "fixture"));
        }
    }
}
