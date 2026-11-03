using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentDock.Desktop;
using Xunit;

namespace AgentDock.Tests;

public sealed class DelegationHookTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock hook 中文 " + Guid.NewGuid().ToString("N"));
    private string CodexHome => Path.Combine(root, "codex home");
    private string Agents => Path.Combine(CodexHome, "AGENTS.md");
    private GlobalRouting Routing => new(CodexHome, Path.Combine(root, "data"));
    private static string HookDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../packaging/plugin/plugins/agentdock/hooks"));

    public DelegationHookTests() => Directory.CreateDirectory(CodexHome);

    public static IEnumerable<object[]> Shells()
    {
        if (OperatingSystem.IsWindows()) yield return ["powershell.exe", "session-start.ps1"];
        else yield return ["/bin/sh", "session-start.sh"];
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SH") is { Length: > 0 } shell)
            yield return [shell, "session-start.sh"];
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task Loads_current_documents_only_while_routing_is_enabled(string shell, string script)
    {
        Assert.Empty(await Run(shell, script));
        Routing.Enable();
        File.AppendAllText(Path.Combine(CodexHome, "AGENTDOCK.md"), "\nUser rule: 中文 edited rule\n");
        var before = Directory.GetFiles(CodexHome).ToDictionary(path => path, File.ReadAllText);
        var output = await Run(shell, script);
        Assert.Contains("User rule: 中文 edited rule", output);
        Assert.Contains("# Subagent roles", output);
        Assert.Contains("grants no task or tool permissions", output);
        foreach (var (path, contents) in before) Assert.Equal(contents, File.ReadAllText(path));
        Routing.Disable();
        Assert.True(File.Exists(Path.Combine(CodexHome, "AGENTDOCK.md")));
        Assert.Empty(await Run(shell, script));
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task Respects_override_precedence_and_user_managed_entry_with_bom(string shell, string script)
    {
        Routing.Enable();
        var alternate = Path.Combine(CodexHome, "AGENTS.override.md");
        File.WriteAllText(alternate, "# Unrelated active rules\n", Encoding.Unicode);
        Assert.Empty(await Run(shell, script));
        File.WriteAllText(alternate, " \r\n", Encoding.UTF8);
        Assert.Contains("# AgentDock delegation", await Run(shell, script));
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32, new UTF32Encoding(true, true) })
        {
            File.WriteAllText(alternate, GlobalRouting.EntryInstructions.Replace("\n", "\r\n"), encoding);
            Assert.Contains("AGENTS.override.md", await Run(shell, script));
        }
    }

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task Missing_empty_or_ambiguous_rules_do_not_load_partial_instructions(string shell, string script)
    {
        Routing.Enable();
        var roles = Path.Combine(CodexHome, "AGENT-ROLES.md");
        File.Delete(roles);
        var output = await Run(shell, script);
        Assert.Contains("could not be read", output);
        Assert.DoesNotContain("# AgentDock delegation", output);
        File.WriteAllText(roles, " \n");
        Assert.Contains("could not be read", await Run(shell, script));
        File.AppendAllText(Agents, "\n" + GlobalRouting.EntryInstructions);
        Assert.Empty(await Run(shell, script));
    }

    [Fact]
    public void Plugin_hook_is_limited_to_session_context_events()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(HookDirectory, "hooks.json")));
        var hook = Assert.Single(config.RootElement.GetProperty("hooks").EnumerateObject());
        Assert.Equal("SessionStart", hook.Name);
        var group = Assert.Single(hook.Value.EnumerateArray());
        var matcher = group.GetProperty("matcher").GetString()!;
        foreach (var source in new[] { "startup", "resume", "clear", "compact", "fork" }) Assert.Matches(matcher, source);
        Assert.DoesNotMatch(matcher, "PermissionRequest");
        Assert.Single(group.GetProperty("hooks").EnumerateArray());
    }

    private async Task<string> Run(string shell, string script)
    {
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = root
        };
        if (script.EndsWith(".ps1", StringComparison.Ordinal))
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(arg);
        else if (OperatingSystem.IsWindows())
            start.Environment["PATH"] = Path.GetDirectoryName(shell) + Path.PathSeparator + start.Environment["PATH"];
        start.ArgumentList.Add(Path.Combine(HookDirectory, script));
        start.Environment["CODEX_HOME"] = CodexHome;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await error);
        return await output;
    }

    public void Dispose() => Directory.Delete(root, true);
}
