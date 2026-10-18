using AgentDock.Desktop;
using Xunit;

namespace AgentDock.Tests;

public sealed class CodexDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-discovery-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ChatGPT.app")]
    [InlineData("Codex.app")]
    public void Selected_application_resolves_its_bundled_cli(string application)
    {
        var bundle = Path.Combine(root, application);
        var cli = CreateFile(bundle, "Contents", "Resources", "codex");
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), cli);

        // The onboarding file picker passes the selected .app path to this property.
        integration.Executable = bundle;

        Assert.Equal(cli, integration.Executable);
    }

    [Theory]
    [InlineData("ChatGPT.app", "system")]
    [InlineData("Codex.app", "system")]
    [InlineData("ChatGPT.app", "user")]
    [InlineData("Codex.app", "user")]
    public void Discovers_both_names_in_system_and_user_applications(string application, string location)
    {
        var system = Path.Combine(root, "system", "Applications");
        var user = Path.Combine(root, "user", "Applications");
        var cli = CreateFile(location == "system" ? system : user, application, "Contents", "Resources", "codex");

        Assert.Equal(cli, CodexIntegration.FindBundledExecutable(system, user));
    }

    [Fact]
    public void App_without_cli_does_not_hide_a_usable_legacy_install()
    {
        var applications = Path.Combine(root, "Applications");
        CreateFile(applications, "ChatGPT.app", "Contents", "MacOS", "ChatGPT");
        var cli = CreateFile(applications, "Codex.app", "Contents", "Resources", "codex");

        Assert.Equal(cli, CodexIntegration.FindBundledExecutable(applications));
    }

    [Theory]
    [InlineData("Resources")]
    [InlineData("MacOS")]
    public void Explicit_bundle_path_supports_spaces_and_a_trailing_separator(string directory)
    {
        var bundle = Path.Combine(root, "Custom location", "ChatGPT.app");
        var cli = CreateFile(bundle, "Contents", directory, "codex");
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), bundle + Path.DirectorySeparatorChar);

        Assert.Equal(cli, integration.Executable);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("codex.exe")]
    public void Direct_cli_selection_is_preserved(string name)
    {
        var cli = CreateFile(root, "bin", name);
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), cli);

        Assert.Equal(cli, integration.Executable);
    }

    [Fact]
    public async Task Gui_only_bundle_is_reported_without_launching_the_gui_or_writing_config()
    {
        var bundle = Path.Combine(root, "ChatGPT.app");
        CreateFile(bundle, "Contents", "MacOS", "ChatGPT");
        var home = Path.Combine(root, "home");
        var integration = new CodexIntegration(Path.Combine(root, "data"), home, bundle, () => null);

        var status = await integration.InspectAsync(CancellationToken.None);

        Assert.False(status.Registered);
        Assert.Contains("所选应用包中未找到 Codex", status.Message);
        Assert.Null(CodexIntegration.ResolveExecutable(bundle));
        Assert.False(Directory.Exists(home));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("gui-only")]
    [InlineData("moved")]
    public void Recheck_recovers_after_installation_or_an_invalid_previous_selection(string scenario)
    {
        var applications = Path.Combine(root, "Applications");
        var previous = Path.Combine(root, "previous", "Codex.app");
        string? selection = null;
        if (scenario == "gui-only")
        {
            CreateFile(previous, "Contents", "MacOS", "ChatGPT");
            selection = previous;
        }
        else if (scenario == "moved") selection = CreateFile(previous, "Contents", "Resources", "codex");
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), selection,
            () => CodexIntegration.FindBundledExecutable(applications));
        if (scenario == "moved") File.Delete(selection!);
        var cli = CreateFile(applications, "ChatGPT.app", "Contents", "Resources", "codex");

        Assert.Equal(cli, integration.RefreshExecutable());
        Assert.Equal(cli, integration.Executable);
    }

    [Fact]
    public void Recheck_preserves_a_valid_manual_selection()
    {
        var manual = CreateFile(root, "custom", "codex");
        var discovered = CreateFile(root, "Applications", "ChatGPT.app", "Contents", "Resources", "codex");
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), manual, () => discovered);

        Assert.Equal(manual, integration.RefreshExecutable());
    }

    [Fact]
    public void Renamed_user_application_precedes_legacy_system_application()
    {
        var system = Path.Combine(root, "system", "Applications");
        var user = Path.Combine(root, "user", "Applications");
        CreateFile(system, "Codex.app", "Contents", "Resources", "codex");
        var cli = CreateFile(user, "ChatGPT.app", "Contents", "Resources", "codex");

        Assert.Equal(cli, CodexIntegration.FindBundledExecutable(system, user));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gui_discovery_finds_nvm_cli_without_a_shell_path(bool customNvmDirectory)
    {
        var userHome = Path.Combine(root, "user");
        var nvmDirectory = customNvmDirectory ? Path.Combine(root, "custom nvm") : Path.Combine(userHome, ".nvm");
        CreateFile(nvmDirectory, "versions", "node", "v9.9.0", "bin", "codex");
        var cli = CreateFile(nvmDirectory, "versions", "node", "v25.0.0", "bin", "codex");
        CreateFile(nvmDirectory, "versions", "node", "v26.0.0", "bin", "node");
        CreateFile(nvmDirectory, "versions", "node", "version-invalid", "bin", "codex");
        CreateFile(userHome, "Applications", "ChatGPT.app", "Contents", "Resources", "codex-classic.wav");

        Assert.Equal(cli, CodexIntegration.FindUnixExecutable(userHome, Path.Combine(root, "gui-bin"),
            customNvmDirectory ? nvmDirectory : null, [Path.Combine(userHome, "Applications")], []));
    }

    [Fact]
    public void Existing_path_cli_takes_precedence_over_nvm_versions()
    {
        var cli = CreateFile(root, "path-bin", "codex");
        CreateFile(root, ".nvm", "versions", "node", "v25.0.0", "bin", "codex");

        Assert.Equal(cli, CodexIntegration.FindUnixExecutable(root, Path.GetDirectoryName(cli), null, [], []));
    }

    [Fact]
    public void Missing_nvm_install_returns_no_cli()
    {
        Assert.Null(CodexIntegration.FindUnixExecutable(root, Path.Combine(root, "gui-bin"), null, [], []));
    }

    [Fact]
    public void Standalone_cli_keeps_the_inherited_path()
    {
        var cli = CreateFile(root, "standalone", "codex");
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), cli);

        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), integration.CreateStartInfo(["plugin", "list", "--json"]).Environment["PATH"]);
    }

    [Fact]
    public void Nvm_cli_launch_inherits_its_node_directory_without_changing_the_parent_environment()
    {
        var cli = CreateFile(root, ".nvm", "versions", "node", "v25.0.0", "bin", "codex");
        var bin = Path.GetDirectoryName(cli)!;
        CreateFile(bin, "node");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), cli);

        var start = integration.CreateStartInfo(["plugin", "list", "--json"]);

        Assert.Equal(bin + Path.PathSeparator + originalPath, start.Environment["PATH"]);
        Assert.Equal(originalPath, Environment.GetEnvironmentVariable("PATH"));
        Assert.Equal(cli, start.FileName);
        Assert.Equal(integration.CodexHome, start.Environment["CODEX_HOME"]);
        Assert.Equal(new[] { "plugin", "list", "--json" }, start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [MacPathFact]
    public async Task Mac_npm_style_symlink_launches_with_its_sibling_node()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var bin = Path.Combine(root, "nvm with spaces", "versions", "node", "v25.0.0", "bin");
        var script = CreateFile(root, "package", "codex.js");
        File.WriteAllText(script, "#!/usr/bin/env node\n");
        var node = CreateFile(bin, "node");
        File.WriteAllText(node, "#!/bin/sh\nprintf '%s' '{\"installed\":[]}'\n");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(script, mode);
        File.SetUnixFileMode(node, mode);
        var cli = Path.Combine(bin, "codex");
        File.CreateSymbolicLink(cli, script);
        var integration = new CodexIntegration(Path.Combine(root, "data"), Path.Combine(root, "home"), cli);

        var status = await integration.InspectAsync(CancellationToken.None);

        Assert.Equal(cli, status.Executable);
        Assert.False(status.Registered);
        Assert.Contains("已找到 Codex", status.Message);
    }

    private static string CreateFile(params string[] segments)
    {
        var path = Path.Combine(segments);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture; not executed");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
