using System.Text;
using System.Text.Json;
using AgentDock.Desktop;
using Xunit;

namespace AgentDock.Tests;

public sealed class OnboardingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-onboarding-" + Guid.NewGuid().ToString("N"));
    private string CodexHome => Path.Combine(root, "codex");
    private string DataHome => Path.Combine(root, "data");
    private string Agents => Path.Combine(CodexHome, "AGENTS.md");
    private GlobalRouting Store => new(CodexHome, DataHome);
    private string Journal => Path.Combine(DataHome, "onboarding-routing-backup.json");
    private const string ManagedV1 = """
        <!-- external-agent-routing:start -->
        <!-- agentdock:managed-v1 -->
        ## External agent routing

        Keep the primary agent's configured model and ChatGPT authentication. Native collaboration.spawn_agent remains available only for architect, with its existing role settings.

        Delegate clear, narrowly scoped, independent subtasks for default, explorer, worker, reviewer and verifier through AgentDock MCP start_task. Supply an absolute cwd, role, unique request_id, explicit task scope, owned files and acceptance criteria. The main agent remains responsible for planning, integration and final verification. Keep atomic tasks, browser, desktop and app-connector work with the main agent.

        Use wait_task/get_task with event cursors until each accepted task reaches a terminal state; drain hasMore pages. Do not treat acceptance or a running task as completion. Reuse request_id only for an identical retry; failed or interrupted tasks need an explicit new user request before dispatching again. Do not switch models or backends automatically. If AgentDock is unavailable, report the blocker and continue independent main-agent work.

        These defaults apply across projects, subject to higher-priority instructions and project-specific restrictions. Send only task-relevant code, documentation and redacted evidence; never send secrets, credentials or unrelated data.
        <!-- external-agent-routing:end -->
        """;
    public OnboardingTests() => Directory.CreateDirectory(CodexHome);

    [Fact]
    public void Enable_is_idempotent_and_disable_preserves_surrounding_edits()
    {
        File.WriteAllText(Agents, "# User preferences\nPreserve this.\n");
        Assert.False(Store.Inspect().Enabled);
        Assert.True(Store.Enable().Enabled);
        var first = File.ReadAllBytes(Agents);
        Store.Enable();
        Assert.Equal(first, File.ReadAllBytes(Agents));
        File.AppendAllText(Agents, "\nA later user addition.\n");
        Assert.False(Store.Disable().Enabled);
        var final = File.ReadAllText(Agents);
        Assert.Contains("Preserve this.", final);
        Assert.Contains("A later user addition.", final);
        Assert.DoesNotContain("agentdock:managed", final);
        Assert.False(File.Exists(Path.Combine(CodexHome, "AGENTS.override.md")));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  \n", false)]
    [InlineData("# Override instructions\n", true)]
    public void Uses_first_nonempty_global_instruction_file(string content, bool overrideActive)
    {
        var alternate = Path.Combine(CodexHome, "AGENTS.override.md");
        File.WriteAllText(Agents, "Original");
        File.WriteAllText(alternate, content);
        Assert.Equal(overrideActive ? alternate : Agents, Store.Enable().Path);
        Assert.Equal(overrideActive ? "Original" : content, File.ReadAllText(overrideActive ? Agents : alternate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("original")]
    [InlineData("original\r\n")]
    public void Repeated_enable_disable_roundtrips_exact_original(string original)
    {
        File.WriteAllText(Agents, original);
        for (var i = 0; i < 3; i++) { Store.Enable(); Store.Disable(); }
        Assert.Equal(original, File.ReadAllText(Agents));
    }

    [Fact]
    public void Invalid_utf8_reports_a_problem_without_replacing_file()
    {
        byte[] bytes = [0xFF, 0x00, 0x80];
        File.WriteAllBytes(Agents, bytes);
        Assert.NotNull(Store.Inspect().Problem);
        Assert.Throws<InvalidOperationException>(() => Store.Enable());
        Assert.Equal(bytes, File.ReadAllBytes(Agents));
    }

    [Fact]
    public void Restores_known_transition_block_and_preserves_utf8_bom_and_crlf()
    {
        var original = """
            <!-- external-agent-routing:start -->
            ## External agent routing

            Keep the primary agent's configured model and ChatGPT authentication. Native collaboration.spawn_agent remains available only for architect, with its existing role settings.

            For default, explorer, worker, reviewer and verifier, handle work in the primary agent until the user selects and validates a replacement external-agent integration. Do not switch models or backends automatically. Keep browser, desktop and app-connector work with the primary agent.
            <!-- external-agent-routing:end -->
            """.Replace("\n", "\r\n");
        File.WriteAllText(Agents, "Before\r\n" + original + "\r\nAfter", new UTF8Encoding(true));
        var before = File.ReadAllBytes(Agents);
        Store.Enable();
        Assert.True(File.ReadAllBytes(Agents).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.DoesNotContain("\n", File.ReadAllText(Agents).Replace("\r\n", ""));
        Store.Disable();
        Assert.Equal(before, File.ReadAllBytes(Agents));
    }

    [Theory]
    [InlineData("<!-- external-agent-routing:start -->unfinished")]
    [InlineData("<!-- external-agent-routing:end -->")]
    [InlineData("<!-- external-agent-routing:start -->other policy<!-- external-agent-routing:end -->")]
    public void Refuses_unknown_or_malformed_policy(string content)
    {
        File.WriteAllText(Agents, content);
        Assert.NotNull(Store.Inspect().Problem);
        Assert.Throws<InvalidOperationException>(() => Store.Enable());
        Assert.Equal(content, File.ReadAllText(Agents));
    }

    [Fact]
    public void Refuses_duplicated_or_manually_modified_managed_block()
    {
        Store.Enable();
        File.AppendAllText(Agents, GlobalRouting.Instructions);
        Assert.NotNull(Store.Inspect().Problem);
        Assert.Throws<InvalidOperationException>(() => Store.Disable());
        File.WriteAllText(Agents, GlobalRouting.Instructions.Replace("Keep atomic tasks", "Keep custom tasks"));
        Assert.Throws<InvalidOperationException>(() => Store.Disable());
    }

    [Fact]
    public void Switching_active_file_does_not_overwrite_restore_journal()
    {
        Store.Enable();
        var journal = File.ReadAllBytes(Path.Combine(DataHome, "onboarding-routing-backup.json"));
        File.WriteAllText(Path.Combine(CodexHome, "AGENTS.override.md"), "New scope");
        Assert.NotNull(Store.Inspect().Problem);
        Assert.Throws<InvalidOperationException>(() => Store.Enable());
        Assert.Equal(journal, File.ReadAllBytes(Path.Combine(DataHome, "onboarding-routing-backup.json")));
    }

    [Fact]
    public void Lost_restore_journal_is_reported_without_destroying_rule()
    {
        Store.Enable();
        File.Delete(Path.Combine(DataHome, "onboarding-routing-backup.json"));
        Assert.True(Store.Inspect().Enabled);
        Assert.NotNull(Store.Inspect().Problem);
        Assert.Throws<InvalidOperationException>(() => Store.Disable());
    }

    [Fact]
    public void New_install_writes_external_profile_reporting_instructions()
    {
        Store.Enable();
        var written = File.ReadAllText(Agents);
        Assert.Contains("SUBAGENT_DISPATCH", written);
        Assert.Contains("status.executionProfile.model", written);
        Assert.Contains("status.executionProfile.reasoningEffort", written);
        Assert.Contains("instead of native Codex agent TOMLs", written);
        Assert.DoesNotContain("deepseek-v4.1-flash", written); // Runtime metadata, not a cached model/version.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_upgrade_preserves_original_policy_encoding_and_later_edits(bool replacedPolicy)
    {
        var legacy = ManagedV1.Replace("\n", "\r\n");
        var previous = replacedPolicy ? "Original external policy" : null;
        var addition = "\r\n" + legacy + "\r\n";
        WriteJournal(new() { Path = Agents, OriginalBlock = previous, WrittenBlock = legacy, AppendedText = replacedPolicy ? null : addition });
        File.WriteAllText(Agents, "User preferences" + addition + "Later user edit", new UTF8Encoding(true));
        var state = Store.UpgradeEnabled();
        Assert.True(state.Enabled);
        Assert.Null(state.Problem);
        var first = File.ReadAllBytes(Agents);
        Assert.True(first.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Contains("status.executionProfile.model", File.ReadAllText(Agents));
        Assert.DoesNotContain("\n", File.ReadAllText(Agents).Replace("\r\n", ""));
        Store.UpgradeEnabled();
        Assert.Equal(first, File.ReadAllBytes(Agents));
        Store.Disable();
        Assert.Equal(replacedPolicy ? "User preferences\r\nOriginal external policy\r\nLater user edit" : "User preferencesLater user edit", File.ReadAllText(Agents));
        Assert.False(File.Exists(Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_upgrade_resumes_before_or_after_rule_file_write(bool ruleAlreadyWritten)
    {
        WriteJournal(new() { Path = Agents, OriginalBlock = null, WrittenBlock = GlobalRouting.Instructions,
            PreviousWrittenBlock = ManagedV1, AppendedText = "\n" + GlobalRouting.Instructions + "\n" });
        File.WriteAllText(Agents, "Preserve\n" + (ruleAlreadyWritten ? GlobalRouting.Instructions : ManagedV1) + "\n");
        Assert.Null(Store.UpgradeEnabled().Problem);
        var journal = JsonSerializer.Deserialize<GlobalRouting.RoutingBackup>(File.ReadAllBytes(Journal), AgentDock.Core.JsonDefaults.Options)!;
        Assert.Null(journal.PreviousWrittenBlock);
        Assert.Contains("status.executionProfile.reasoningEffort", File.ReadAllText(Agents));
        Store.Disable();
        Assert.Equal("Preserve", File.ReadAllText(Agents));
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("disabled")]
    [InlineData("missing-journal")]
    [InlineData("modified-rule")]
    [InlineData("mismatched-journal")]
    [InlineData("changed-active-file")]
    public void Startup_upgrade_never_enables_or_overwrites_unowned_rules(string scenario)
    {
        if (scenario == "fresh")
        {
            Assert.False(Store.UpgradeEnabled().Enabled);
            Assert.False(File.Exists(Agents));
            Assert.False(File.Exists(Journal));
            return;
        }
        File.WriteAllText(Agents, scenario == "disabled" ? "User preferences" : ManagedV1);
        if (scenario is not ("disabled" or "missing-journal"))
            WriteJournal(new() { Path = Agents, WrittenBlock = scenario == "mismatched-journal" ? "unrelated" : ManagedV1 });
        if (scenario == "modified-rule") File.WriteAllText(Agents, ManagedV1.Replace("Keep atomic tasks", "Keep my tasks"));
        var alternate = Path.Combine(CodexHome, "AGENTS.override.md");
        if (scenario == "changed-active-file") File.WriteAllText(alternate, "New active file");
        var before = File.ReadAllBytes(Agents);
        var journalBefore = File.Exists(Journal) ? File.ReadAllBytes(Journal) : null;
        var state = Store.UpgradeEnabled();
        if (scenario != "disabled") Assert.NotNull(state.Problem);
        Assert.Equal(before, File.ReadAllBytes(Agents));
        Assert.Equal(journalBefore, File.Exists(Journal) ? File.ReadAllBytes(Journal) : null);
        if (scenario == "changed-active-file") Assert.Equal("New active file", File.ReadAllText(alternate));
    }

    private void WriteJournal(GlobalRouting.RoutingBackup backup)
    {
        Directory.CreateDirectory(DataHome);
        File.WriteAllText(Journal, JsonSerializer.Serialize(backup, AgentDock.Core.JsonDefaults.Options));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":null,\"writtenBlock\":null}")]
    public void Incomplete_restore_record_is_reported_without_startup_failure(string content)
    {
        File.WriteAllText(Agents, ManagedV1);
        Directory.CreateDirectory(DataHome);
        File.WriteAllText(Journal, content);
        Assert.NotNull(Store.UpgradeEnabled().Problem);
        Assert.Equal(ManagedV1, File.ReadAllText(Agents));
        Assert.Equal(content, File.ReadAllText(Journal));
    }

    [Fact]
    public void Fresh_install_and_corrupt_progress_both_show_guide()
    {
        Assert.False(OnboardingProgress.IsComplete(DataHome));
        OnboardingProgress.Complete(DataHome);
        Assert.True(OnboardingProgress.IsComplete(DataHome));
        File.WriteAllText(Path.Combine(DataHome, "onboarding.json"), "broken");
        Assert.False(OnboardingProgress.IsComplete(DataHome));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void Plugin_status_requires_installed_enabled_and_current_source(bool enabled, bool installed, bool sameSource, bool ready)
    {
        var market = Path.Combine(root, "plugin");
        var json = JsonSerializer.Serialize(new { installed = new[] { new { pluginId = "agentdock@agentdock-local", enabled, installed, marketplaceSource = new { source = sameSource ? market : root } } } });
        Assert.Equal(ready, CodexIntegration.ParseStatus(json, "codex", market).Registered);
    }

    [MacPathFact]
    public void Mac_plugin_status_resolves_a_symlinked_install_directory()
    {
        var real = Directory.CreateDirectory(Path.Combine(root, "actual", "plugin")).FullName;
        var alias = Path.Combine(root, "install-link");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(real)!);
        var json = JsonSerializer.Serialize(new { installed = new[] { new { pluginId = "agentdock@agentdock-local", enabled = true, installed = true, marketplaceSource = new { source = real } } } });

        Assert.True(CodexIntegration.ParseStatus(json, "codex", Path.Combine(alias, "plugin")).Registered);
    }

    [MacPathFact]
    public void Mac_plugin_status_uses_the_volumes_actual_case_semantics()
    {
        var real = Directory.CreateDirectory(Path.Combine(root, "MixedCase", "plugin")).FullName;
        var alternate = Path.Combine(root, "mixedcase", "plugin");
        var sameDirectory = Directory.Exists(alternate);
        if (!sameDirectory) Directory.CreateDirectory(alternate);
        var json = JsonSerializer.Serialize(new { installed = new[] { new { pluginId = "agentdock@agentdock-local", enabled = true, installed = true, marketplaceSource = new { source = real } } } });

        Assert.Equal(sameDirectory, CodexIntegration.ParseStatus(json, "codex", alternate).Registered);
    }

    [Fact]
    public void Missing_plugin_is_not_registered_and_unknown_contract_is_not_ready()
    {
        Assert.False(CodexIntegration.ParseStatus("{\"installed\": []}", "codex", root).Registered);
        Assert.Throws<InvalidOperationException>(() => CodexIntegration.ParseStatus("{}", "codex", root));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
