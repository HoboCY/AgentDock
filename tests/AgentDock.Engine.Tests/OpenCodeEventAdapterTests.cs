using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class OpenCodeEventAdapterTests
{
    [Fact]
    public void TextReasoningAndUsageFlowUntilTerminalStop()
    {
        var adapter = new OpenCodeEventAdapter();
        adapter.FeedAll(Fixtures.Event("step_start", Fixtures.Part("step-start")));
        var reasoning = adapter.FeedAll(Fixtures.Event("reasoning",
            Fixtures.Part("reasoning", ("text", "inspect the repository"))));
        Assert.Equal("reasoning", Assert.Single(reasoning).Kind);
        Assert.Equal("inspect the repository", reasoning[0].Text);

        var text = adapter.FeedAll(Fixtures.Event("text", Fixtures.Part("text", ("text", "done"))));
        Assert.Equal("text", Assert.Single(text).Kind);
        Assert.Equal("done", text[0].Text);
        Assert.Equal("done", adapter.Result);
        Assert.False(adapter.CanComplete(0));

        var usage = adapter.FeedAll(Fixtures.StepFinish("stop", tokensJson: Fixtures.Tokens(10, 4, 1, 2, 3)));
        var usageEvent = Assert.Single(usage);
        Assert.Equal("usage", usageEvent.Kind);
        Assert.Equal(15, usageEvent.InputTokens);
        Assert.Equal(5, usageEvent.OutputTokens);
        Assert.NotNull(usageEvent.UsageId);
        Assert.Null(usageEvent.GenerationSeconds);
        Assert.True(adapter.CanComplete(0));
        Assert.False(adapter.CanComplete(1));
        Assert.Equal(Fixtures.Session, adapter.SessionId);
    }

    [Fact]
    public void LengthAndMaxTokensAndEarlyEofAreNeverSuccess()
    {
        var length = new OpenCodeEventAdapter();
        length.FeedAll(Fixtures.StepFinish("length", tokensJson: Fixtures.Tokens(1, 1, 0, 0, 0)));
        Assert.False(length.CanComplete(0));

        var eof = new OpenCodeEventAdapter();
        eof.FeedAll(Fixtures.Event("text", Fixtures.Part("text", ("text", "partial"))));
        Assert.False(eof.CanComplete(0));

        var failed = new OpenCodeEventAdapter();
        failed.FeedAll(Fixtures.StepFinish("stop", tokensJson: Fixtures.Tokens(1, 1, 0, 0, 0)));
        Assert.True(failed.CanComplete(0));
        Assert.False(failed.CanComplete(1));
    }

    [Fact]
    public void TerminalErrorEmitsErrorEventAndBlocksCompletion()
    {
        var adapter = new OpenCodeEventAdapter();
        var reported = adapter.FeedAll(Fixtures.Event("error", null,
            new { name = "UnknownError", data = new { message = "provider unavailable" } }));
        var error = Assert.Single(reported);
        Assert.Equal("error", error.Kind);
        Assert.Contains("provider unavailable", error.Text);
        adapter.FeedAll(Fixtures.StepFinish("stop", tokensJson: Fixtures.Tokens(1, 1, 0, 0, 0)));
        Assert.False(adapter.CanComplete(0));
        Assert.Contains("provider unavailable", adapter.Failure);
    }

    [Fact]
    public void ToolEventsAreBoundedAndAStopIsCancelledByLaterToolUse()
    {
        var adapter = new OpenCodeEventAdapter();
        var events = adapter.FeedAll(Fixtures.Event("tool_use", Fixtures.Part("tool",
            ("id", "prt_bash"), ("callID", "call_bash"), ("tool", "bash"),
            ("state", new
            {
                status = "completed",
                input = new { command = "python -V" },
                output = "Python 3.11",
                metadata = new { },
            }))));
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal("tool", item.Kind));
        Assert.Contains("python -V", events[0].Text);
        Assert.Contains("Python 3.11", events[1].Text);

        adapter.FeedAll(Fixtures.StepFinish("stop", tokensJson: Fixtures.Tokens(1, 1, 0, 0, 0)));
        Assert.True(adapter.CanComplete(0));

        adapter.FeedAll(Fixtures.Event("tool_use", Fixtures.Part("tool",
            ("id", "prt_read"), ("callID", "call_read"), ("tool", "read"),
            ("state", new
            {
                status = "completed",
                input = new { filePath = "src/main.py" },
                output = "print('ok')",
                metadata = new { },
            }))));
        Assert.False(adapter.CanComplete(0));
    }

    [Fact]
    public void FileChangeToolPathsAppearInToolEvents()
    {
        var adapter = new OpenCodeEventAdapter();
        var events = adapter.FeedAll(Fixtures.Event("tool_use", Fixtures.Part("tool",
            ("id", "prt_edit"), ("callID", "call_edit"), ("tool", "edit"),
            ("state", new
            {
                status = "completed",
                input = new { filePath = "src/main.py", oldString = "x", newString = "y" },
                output = "updated",
                metadata = new { },
            }))));
        Assert.Contains("src/main.py", events[0].Text);

        var patch = adapter.FeedAll(Fixtures.Event("tool_use", Fixtures.Part("tool",
            ("id", "prt_patch"), ("callID", "call_patch"), ("tool", "apply_patch"),
            ("state", new
            {
                status = "completed",
                input = new { patch = "*** Begin Patch\n*** Update File: src/other.py\n@@\n-x\n+y\n*** End Patch" },
                output = "ok",
                metadata = new { },
            }))));
        Assert.Contains("src/other.py", patch[0].Text);
    }

    [Fact]
    public void UsageIsReportedPerPartWithComponentsAggregatedAndDeduplicated()
    {
        var adapter = new OpenCodeEventAdapter();
        var first = adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_s1",
            tokensJson: Fixtures.Tokens(10, 4, 1, 2, 3)));
        Assert.Equal(15, Assert.Single(first).InputTokens);
        Assert.Equal(5, first[0].OutputTokens);

        // The second part reports only its own components; the host sums parts.
        var second = adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_s2",
            tokensJson: Fixtures.Tokens(5, 2, 1, 1, 0)));
        Assert.Equal(6, Assert.Single(second).InputTokens);
        Assert.Equal(3, second[0].OutputTokens);

        // Replay is ignored; a revision replaces that phase rather than adding a new phase.
        Assert.Empty(adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_s1",
            tokensJson: Fixtures.Tokens(10, 4, 1, 2, 3))));
        var revised = Assert.Single(adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_s1",
            tokensJson: """{"output":8}""")));
        Assert.Equal(15, revised.InputTokens);
        Assert.Equal(9, revised.OutputTokens);
        Assert.Equal("prt_s1", first[0].UsageId);

        // A part that reports only input keeps output unknown.
        var third = adapter.FeedAll(Fixtures.StepFinish("stop", id: "prt_s3", tokensJson: """{"input":5}"""));
        Assert.Equal(5, Assert.Single(third).InputTokens);
        Assert.Null(third[0].OutputTokens);
    }

    [Fact]
    public void CacheOnlyPartsContributeInputWithoutAFakeOutput()
    {
        var adapter = new OpenCodeEventAdapter();
        var events = adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_cache",
            tokensJson: """{"cache":{"read":7,"write":3}}"""));
        var usage = Assert.Single(events);
        Assert.Equal(10, usage.InputTokens);
        Assert.Null(usage.OutputTokens);
    }

    [Fact]
    public void AbsentUsageStaysUnknownAndReportedZeroStaysZero()
    {
        var adapter = new OpenCodeEventAdapter();
        Assert.Empty(adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_none", tokensJson: "{}")));

        var partial = adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_partial",
            tokensJson: """{"output":4}"""));
        Assert.Null(Assert.Single(partial).InputTokens);
        Assert.Equal(4, partial[0].OutputTokens);

        var zero = adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_zero",
            tokensJson: """{"input":0,"output":0}"""));
        Assert.Equal(0, Assert.Single(zero).InputTokens);
        Assert.Equal(0, zero[0].OutputTokens);
    }

    [Fact]
    public void InvalidTokenValuesAreRejectedWithoutFakeZero()
    {
        var adapter = new OpenCodeEventAdapter();
        Assert.Empty(adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_bad",
            tokensJson: """{"input":-1,"output":true,"reasoning":-1,"cache":{"read":-2}}""")));
        Assert.False(adapter.CanComplete(0));

        var mixed = adapter.FeedAll(Fixtures.StepFinish("tool-calls", id: "prt_mixed",
            tokensJson: """{"input":-5,"output":3,"reasoning":0}"""));
        Assert.Null(Assert.Single(mixed).InputTokens);
        Assert.Equal(3, mixed[0].OutputTokens);
    }

    [Fact]
    public void MalformedAndUnknownEventsAreTolerated()
    {
        var adapter = new OpenCodeEventAdapter();
        Assert.Empty(adapter.FeedAll("", "not json", "null", "{}", """{"type":"future_event","properties":[]}""",
            """{"type":"tool_use","part":{"type":"tool","state":"bad"}}"""));
        var started = adapter.FeedAll("""{"type":"step_start","part":"not-an-object"}""");
        Assert.Empty(started);
        Assert.False(adapter.CanComplete(0));
    }

    [Fact]
    public void LargeTextIsBounded()
    {
        var adapter = new OpenCodeEventAdapter();
        var text = adapter.FeedAll(Fixtures.Event("text", Fixtures.Part("text", ("text", new string('x', 100_000)))));
        Assert.True(Assert.Single(text).Text.Length <= OpenCodeEventAdapter.MaxText + 1);
    }

    [Fact]
    public void ToolCompletionRequiresStatusAndDoesNotDuplicate()
    {
        var adapter = new OpenCodeEventAdapter();
        var running = adapter.FeedAll(Fixtures.Event("tool_use", Fixtures.Part("tool",
            ("id", "prt_x"), ("callID", "call_x"), ("tool", "bash"),
            ("state", new { status = "running", input = new { command = "echo hi" } }))));
        Assert.Single(running);

        var completed = adapter.FeedAll(Fixtures.Event("tool_use", Fixtures.Part("tool",
            ("id", "prt_x"), ("callID", "call_x"), ("tool", "bash"),
            ("state", new { status = "completed", input = new { command = "echo hi" }, output = "hi" }))));
        Assert.Single(completed);
        Assert.Contains("completed", completed[0].Text);

        var duplicate = adapter.FeedAll(Fixtures.Event("tool_use", Fixtures.Part("tool",
            ("id", "prt_x"), ("callID", "call_x"), ("tool", "bash"),
            ("state", new { status = "completed", input = new { command = "echo hi" }, output = "hi" }))));
        Assert.Empty(duplicate);
    }
}
