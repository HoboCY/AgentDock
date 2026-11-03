using System.Text.Json;
using AgentDock.Desktop;
using Xunit;

namespace AgentDock.Tests;

public sealed class CodexTaskApprovalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-approval-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "config.toml");
    private CodexIntegration Integration => new(Path.Combine(root, "data"), root, Environment.GetEnvironmentVariable("AGENTDOCK_TEST_CODEX"));

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"tools\":{\"start_task\":{\"approval_mode\":\"approve\"}}}", true)]
    [InlineData("{\"tools\":{\"stop_task\":{\"approval_mode\":\"approve\"}}}", false)]
    [InlineData("{\"default_tools_approval_mode\":\"approve\"}", true)]
    [InlineData("{\"default_tools_approval_mode\":\"approve\",\"tools\":{\"start_task\":{\"approval_mode\":\"prompt\"}}}", false)]
    [InlineData("{\"default_tools_approval_mode\":\"approve\",\"enabled\":false}", false)]
    [InlineData("{\"default_tools_approval_mode\":\"approve\",\"enabled_tools\":[\"status\"]}", false)]
    [InlineData("{\"default_tools_approval_mode\":\"approve\",\"disabled_tools\":[\"start_task\"]}", false)]
    [InlineData("{\"default_tools_approval_mode\":\"approve\",\"enabled_tools\":[\"start_task\"]}", true)]
    public void Readiness_respects_tool_overrides_and_existing_restrictions(string server, bool expected)
    {
        using var document = JsonDocument.Parse("{\"plugins\":{\"agentdock@agentdock-local\":{\"enabled\":true,\"mcp_servers\":{\"agentdock\":" + server + "}}}}");
        Assert.Equal(expected, CodexIntegration.TaskStartApproved(document.RootElement));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"plugins\":{\"agentdock@other\":{\"mcp_servers\":{\"agentdock\":{\"default_tools_approval_mode\":\"approve\"}}}}}")]
    [InlineData("{\"plugins\":{\"agentdock@agentdock-local\":{\"enabled\":false,\"mcp_servers\":{\"agentdock\":{\"default_tools_approval_mode\":\"approve\"}}}}}")]
    public void Missing_different_or_disabled_plugin_is_not_authorized(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(CodexIntegration.TaskStartApproved(document.RootElement));
    }

    [CodexConfigFact]
    public async Task Explicit_grant_and_revoke_persist_using_real_codex_without_changing_other_settings()
    {
        WriteConfig();
        var original = File.ReadAllBytes(ConfigPath);
        Assert.False(await Integration.InspectTaskApprovalAsync(CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(ConfigPath));

        Assert.True(await Integration.SetTaskApprovalAsync(true, CancellationToken.None));
        Assert.True(await Integration.InspectTaskApprovalAsync(CancellationToken.None));
        var granted = File.ReadAllText(ConfigPath);
        Assert.Contains("[plugins.\"agentdock@agentdock-local\".mcp_servers.agentdock.tools.start_task]", granted);
        Assert.Contains("approval_mode = \"approve\"", granted);
        AssertUnrelatedSettings(granted);

        Assert.False(await Integration.SetTaskApprovalAsync(false, CancellationToken.None));
        Assert.False(await Integration.InspectTaskApprovalAsync(CancellationToken.None));
        var revoked = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("approval_mode = \"approve\"", revoked);
        AssertUnrelatedSettings(revoked);
    }

    [CodexConfigFact]
    public async Task Grant_does_not_override_a_disabled_tool_or_report_success()
    {
        WriteConfig();
        File.AppendAllText(ConfigPath, "\n[plugins.\"agentdock@agentdock-local\".mcp_servers.agentdock]\ndisabled_tools = [\"start_task\"]\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Integration.SetTaskApprovalAsync(true, CancellationToken.None));
        Assert.Contains("工具限制", error.Message);
        Assert.False(await Integration.InspectTaskApprovalAsync(CancellationToken.None));
        Assert.Contains("disabled_tools = [\"start_task\"]", File.ReadAllText(ConfigPath));
    }

    [CodexConfigFact]
    public async Task Cancellation_does_not_change_config_or_claim_success()
    {
        WriteConfig();
        var original = File.ReadAllBytes(ConfigPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Integration.SetTaskApprovalAsync(true, cancellation.Token));
        Assert.Contains("取消或超时", error.Message);
        Assert.Equal(original, File.ReadAllBytes(ConfigPath));
    }

    [CodexConfigFact]
    public async Task Invalid_config_is_preserved_and_private_diagnostics_are_not_displayed()
    {
        Directory.CreateDirectory(root);
        const string invalid = "api_key = \"PRIVATE_FIXTURE_NOT_A_REAL_SECRET\"\n[invalid";
        File.WriteAllText(ConfigPath, invalid);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Integration.SetTaskApprovalAsync(true, CancellationToken.None));
        Assert.DoesNotContain("PRIVATE_FIXTURE", error.Message);
        Assert.Equal(invalid, File.ReadAllText(ConfigPath));
    }

    private void WriteConfig()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(ConfigPath, """
            # Preserve these preferences and comments.
            model = "fixture-model"
            approval_policy = "on-request"
            sandbox_mode = "workspace-write"
            [plugins."agentdock@agentdock-local"]
            enabled = true
            [plugins."agentdock@agentdock-local".mcp_servers.agentdock.tools.stop_task]
            approval_mode = "prompt"
            [plugins."other@local"]
            enabled = false
            """);
    }

    private static void AssertUnrelatedSettings(string text)
    {
        Assert.Contains("# Preserve these preferences and comments.", text);
        Assert.Contains("model = \"fixture-model\"", text);
        Assert.Contains("approval_policy = \"on-request\"", text);
        Assert.Contains("sandbox_mode = \"workspace-write\"", text);
        Assert.Contains("[plugins.\"other@local\"]\nenabled = false", text);
        Assert.Contains("[plugins.\"agentdock@agentdock-local\".mcp_servers.agentdock.tools.stop_task]\napproval_mode = \"prompt\"", text);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

public sealed class CodexConfigFactAttribute : FactAttribute
{
    public CodexConfigFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("AGENTDOCK_TEST_CODEX")))
            Skip = "Set AGENTDOCK_TEST_CODEX to a Codex CLI with plugin-scoped tool approval support; uses isolated config only.";
    }
}
