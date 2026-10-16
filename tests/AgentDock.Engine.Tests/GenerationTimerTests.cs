using System.Text;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class GenerationTimerTests
{
    private static byte[] Frame(string payload) => Encoding.UTF8.GetBytes("data: " + payload + "\n\n");

    [Fact]
    public void MultipleEventsDeliveredInOneReadDoNotMeasureParserCpuAsGenerationTime()
    {
        var timer = new GenerationTimer();
        var content = Frame("""{"choices":[{"delta":{"content":"answer"}}]}""");
        var usage = Frame("""{"choices":[],"usage":{"completion_tokens":10}}""");
        timer.Observe([..content, ..usage]);
        Assert.False(timer.TryFinish(out _));
    }

    [Fact]
    public void MeasuresFromFirstOutputDeltaUntilUsageChunk()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"role":"assistant"}}]}"""));
        Assert.False(timer.Started);
        timer.Observe(Frame("""{"choices":[{"delta":{"reasoning_content":"think"}}]}"""));
        Assert.True(timer.Started);
        Thread.Sleep(40);
        timer.Observe(Frame("""{"choices":[{"delta":{}}],"usage":{"prompt_tokens":1,"completion_tokens":2}}"""));
        Assert.True(timer.TryFinish(out var seconds));
        Assert.InRange(seconds, 0.02, 5);
    }

    [Fact]
    public void EmptyContentAndReasoningDoNotStartTheTimer()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"content":""}}]}"""));
        timer.Observe(Frame("""{"choices":[{"delta":{"reasoning_content":""}}]}"""));
        Assert.False(timer.Started);
        timer.Observe(Frame("""[DONE]"""));
        Assert.False(timer.TryFinish(out _));
    }

    [Fact]
    public void ToolCallDeltasDoNotStartTheTimer()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"bash"}}]}}]}"""));
        Assert.False(timer.Started);
        timer.Observe(Frame("""{"choices":[{"delta":{"content":"answer"}}]}"""));
        Assert.True(timer.Started);
    }

    [Fact]
    public void EventsSplitAcrossChunksAreStillRecognized()
    {
        var timer = new GenerationTimer();
        var bytes = Frame("""{"choices":[{"delta":{"content":"hello"}}]}""");
        var split = bytes.Length / 2;
        timer.Observe(bytes.AsSpan(0, split));
        Assert.False(timer.Started);
        timer.Observe(bytes.AsSpan(split));
        Assert.True(timer.Started);
    }

    [Fact]
    public void UsageSplitAcrossChunksClosesTheWindow()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"content":"hello"}}]}"""));
        Thread.Sleep(30);
        var usage = Frame("""{"choices":[{"delta":{}}],"usage":{"prompt_tokens":3,"completion_tokens":4}}""");
        var split = usage.Length - 6; // Inside the usage payload.
        timer.Observe(usage.AsSpan(0, split));
        timer.Observe(usage.AsSpan(split));
        Assert.True(timer.TryFinish(out var seconds));
        Assert.InRange(seconds, 0.01, 5);
    }

    [Fact]
    public void WhitespaceInJsonPayloadsIsParsed()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("{ \"choices\" : [ { \"delta\" : { \"content\" : \"x\" } } ] }"));
        Assert.True(timer.Started);
        timer.Observe(Frame("{ \"choices\" : [], \"usage\" : { \"prompt_tokens\" : 1 } }"));
        Assert.True(timer.TryFinish(out var seconds));
        Assert.InRange(seconds, 0, 5);
    }

    [Fact]
    public void ContentAndUsageInTheSameChunkStartsButDoesNotFabricateADuration()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"content":"answer"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}"""));
        Assert.True(timer.Started);
        timer.Observe(Frame("""[DONE]"""));
        Assert.False(timer.TryFinish(out _));
    }

    [Fact]
    public void RepeatedContentAndUsageChunksMeasureTheWindowBetweenThem()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"content":"a"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""));
        Thread.Sleep(30);
        timer.Observe(Frame("""{"choices":[{"delta":{"content":"b"}}],"usage":{"prompt_tokens":2,"completion_tokens":2}}"""));
        Assert.True(timer.TryFinish(out var seconds));
        Assert.InRange(seconds, 0.01, 5);
    }

    [Fact]
    public void UnfinishedEventAtStreamEndIsIgnored()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"content":"hello"}}]}"""));
        timer.Observe(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"tr"));
        Assert.False(timer.TryFinish(out _));
    }

    [Fact]
    public void WithoutFinishSignalNoDurationIsPublished()
    {
        var timer = new GenerationTimer();
        timer.Observe(Frame("""{"choices":[{"delta":{"content":"partial"}}]}"""));
        Assert.False(timer.TryFinish(out _));
    }

    [Fact]
    public void CrLfDelimitedEventsAreHandled()
    {
        var timer = new GenerationTimer();
        timer.Observe(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\r\n\r\n"));
        Assert.True(timer.Started);
        Thread.Sleep(15);
        timer.Observe(Encoding.UTF8.GetBytes("data: [DONE]\r\n\r\n"));
        Assert.True(timer.TryFinish(out var seconds));
        Assert.InRange(seconds, 0.005, 5);
    }
}
