using System.Text.Json;
using AgentDock.Core;
using AgentDock.Engine;
using Xunit;

namespace AgentDock.Tests;

public sealed class ExecutionProfileTests
{
    [Fact]
    public void EngineExecutionProfileComesFromThePinnedConstants()
    {
        var profile = OpenCodeEngine.ExecutionProfile;

        Assert.Equal(Pins.Provider, profile.Provider);
        Assert.Equal(Pins.Model, profile.Model);
        Assert.Equal(Pins.ReasoningEffort, profile.ReasoningEffort);
        Assert.Equal("opencode-go", profile.Provider);
        Assert.Equal("deepseek-v4.1-flash", profile.Model);
        Assert.Equal("max", profile.ReasoningEffort);
    }

    [Fact]
    public void StatusJsonCarriesCamelCaseExecutionProfileWithoutExtraMetadata()
    {
        var status = new HostStatus("0.1.0", 1234, 0, true, OpenCodeEngine.ExecutionProfile);

        var json = JsonSerializer.Serialize(status, JsonDefaults.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("executionProfile", out var profile), json);
        Assert.False(root.TryGetProperty("ExecutionProfile", out _), json);
        Assert.Equal("opencode-go", profile.GetProperty("provider").GetString());
        Assert.Equal("deepseek-v4.1-flash", profile.GetProperty("model").GetString());
        Assert.Equal("max", profile.GetProperty("reasoningEffort").GetString());
        Assert.Equal(["model", "provider", "reasoningEffort"],
            profile.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void OlderStatusPayloadWithoutExecutionProfileStaysNull()
    {
        const string older = """
            {"version":"0.1.0","processId":42,"unfinishedTasks":1,"canUpdate":false}
            """;

        var status = JsonSerializer.Deserialize<HostStatus>(older, JsonDefaults.Options);

        Assert.NotNull(status);
        Assert.Null(status.ExecutionProfile);
    }

    [Fact]
    public void StatusRoundTripsTheExecutionProfile()
    {
        var status = new HostStatus("0.1.0", 7, 2, false, OpenCodeEngine.ExecutionProfile);

        var copy = JsonSerializer.Deserialize<HostStatus>(
            JsonSerializer.Serialize(status, JsonDefaults.Options), JsonDefaults.Options);

        Assert.Equal(status, copy);
    }
}
