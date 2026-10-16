using System.Text.Json;
using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class ExecutionSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdock-setup-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _project;
    private readonly string _enginePath;
    private readonly string _sandboxPath;
    private readonly string _shellPath;
    private readonly string _runnerPath;

    public ExecutionSetupTests()
    {
        _home = Path.Combine(_root, "home");
        _project = Path.Combine(_root, "project");
        _enginePath = Path.Combine(_root, "tools", "opencode.exe");
        _sandboxPath = Path.Combine(_root, "tools", "codex", "bin", "codex.exe");
        _shellPath = Path.Combine(_root, "tools", "bash.exe");
        _runnerPath = Path.Combine(_root, "tools", "AgentDock.Engine.exe");
        foreach (var file in new[] { _enginePath, _sandboxPath, _shellPath, _runnerPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }
        Directory.CreateDirectory(Path.Combine(_root, "tools", "codex", "codex-path"));
        File.WriteAllText(Path.Combine(_root, "tools", "codex", "codex-path", "rg.exe"), "fixture");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ExecutionTask TaskFor(string role, string prompt = "Do the assigned work.")
    {
        var settings = new DockConfiguration
        {
            EnginePath = _enginePath,
            SandboxPath = _sandboxPath,
            ShellPath = _shellPath,
        };
        return new ExecutionTask("task-1", _project, role, prompt,
            settings.Roles[role], settings);
    }

    private async Task<(ExecutionSetup Setup, GoRelay Relay)> PrepareAsync(string role)
    {
        var relay = await GoRelay.StartAsync("fixture-key-not-real", "job-1",
            new MockHandler((_, _) => System.Threading.Tasks.Task.FromResult(MockHandler.Response("{}", "application/json"))));
        var setup = ExecutionSetup.Prepare(TaskFor(role), _home, relay, _runnerPath);
        return (setup, relay);
    }

    [Fact]
    public async Task WorkerSetupWritesIsolatedConfigWithoutTheCredential()
    {
        var (setup, relay) = await PrepareAsync("worker");
        await using (relay)
        {
            try
            {
                Assert.True(File.Exists(Path.Combine(setup.SandboxHome, "config.toml")));
                Assert.True(File.Exists(Path.Combine(setup.SandboxHome, "opencode.json")));
                Assert.True(File.Exists(setup.LaunchPath));
                Assert.True(File.Exists(Path.Combine(setup.Runtime, "opencode_pipe_compat.cjs")));
                Assert.True(File.Exists(Path.Combine(setup.Runtime, "opencode_pipe_reader.cjs")));
                Assert.True(Directory.Exists(setup.CheckArtifacts));

                var configToml = File.ReadAllText(Path.Combine(setup.SandboxHome, "config.toml"));
                Assert.Contains("[permissions.agentdock.filesystem]", configToml);
                Assert.Contains("\":root\" = \"read\"", configToml);
                Assert.Contains(setup.Runtime.Replace("\\", "\\\\"), configToml);
                Assert.Contains(_project.Replace("\\", "\\\\"), configToml);
                Assert.Contains(Path.GetDirectoryName(_runnerPath)!.Replace("\\", "\\\\"), configToml);
                Assert.Contains("sandbox = \"unelevated\"", configToml);

                var launch = File.ReadAllText(setup.LaunchPath);
                Assert.DoesNotContain(relay.Token, launch);
                Assert.DoesNotContain("fixture-key-not-real", launch);
                Assert.Contains("opencode_pipe_compat.cjs", launch);

                var openCode = File.ReadAllText(Path.Combine(setup.SandboxHome, "opencode.json"));
                Assert.Contains(relay.Token, openCode);
                Assert.DoesNotContain("fixture-key-not-real", openCode);
                Assert.Contains("\"autoupdate\":false", openCode.Replace(" ", ""));
                Assert.Contains("\"plugin\":[]", openCode.Replace(" ", ""));
                Assert.Contains("\"mcp\":{}", openCode.Replace(" ", ""));

                Assert.Equal("write", setup.Filesystem[_project]);
                Assert.Contains("sandbox", setup.SandboxArguments);
                Assert.Contains("agentdock", setup.SandboxArguments);
                Assert.Contains(setup.LaunchPath, setup.SandboxArguments);
                Assert.Equal(_enginePath, setup.OpenCodeArguments[0]);
                Assert.Contains("--variant", setup.OpenCodeArguments);
                Assert.Contains("max", setup.OpenCodeArguments);
                Assert.Equal("1", setup.ChildEnvironment["OPENCODE_DISABLE_AUTOUPDATE"]);
                Assert.StartsWith(setup.Runtime, setup.ChildEnvironment["HOME"]);
                Assert.DoesNotContain("fixture-key-not-real", string.Join('|', setup.ChildEnvironment.Values));
                Assert.DoesNotContain("fixture-key-not-real", string.Join('|', setup.SandboxEnvironment.Values));
                Assert.DoesNotContain("fixture-key-not-real", string.Join('|', setup.OpenCodeArguments));
            }
            finally
            {
                Directory.Delete(setup.JobRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("reviewer")]
    [InlineData("verifier")]
    public async Task ReadOnlyRolesGetAReadOnlyProjectMapping(string role)
    {
        var (setup, relay) = await PrepareAsync(role);
        await using (relay)
        {
            try
            {
                Assert.Equal("read", setup.Filesystem[_project]);
                var openCode = JsonDocument.Parse(File.ReadAllText(Path.Combine(setup.SandboxHome, "opencode.json")));
                Assert.Equal("deny", openCode.RootElement.GetProperty("agent").GetProperty("delegated")
                    .GetProperty("permission").GetProperty("edit").GetString());
                if (role == "verifier")
                {
                    Assert.True(Directory.Exists(setup.CheckArtifacts));
                    Assert.Contains(setup.CheckArtifacts.Replace("\\", "\\\\"),
                        File.ReadAllText(Path.Combine(setup.SandboxHome, "opencode.json")));
                }
            }
            finally
            {
                Directory.Delete(setup.JobRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DefaultAndWorkerRolesAllowProjectWrites()
    {
        var (setup, relay) = await PrepareAsync("default");
        await using (relay)
        {
            try
            {
                Assert.Equal("write", setup.Filesystem[_project]);
                var openCode = JsonDocument.Parse(File.ReadAllText(Path.Combine(setup.SandboxHome, "opencode.json")));
                Assert.Equal("allow", openCode.RootElement.GetProperty("agent").GetProperty("delegated")
                    .GetProperty("permission").GetProperty("edit").GetString());
            }
            finally
            {
                Directory.Delete(setup.JobRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ExplicitRunnerOverrideWins()
    {
        Assert.Equal([_runnerPath], ExecutionSetup.ResolveRunner(_runnerPath, Path.Combine(_root, "does-not-matter")));
    }

    [Fact]
    public void PackagedSiblingEngineWinsOverTheSameFolder()
    {
        var baseDirectory = Path.Combine(_root, "app");
        var packagedDirectory = Path.Combine(_root, "engine");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(packagedDirectory);
        File.WriteAllText(Path.Combine(packagedDirectory, "AgentDock.Engine.exe"), "fixture");
        File.WriteAllText(Path.Combine(baseDirectory, "AgentDock.Engine.exe"), "fixture");

        var runner = ExecutionSetup.ResolveRunner(null, baseDirectory);

        Assert.Equal(Path.Combine(packagedDirectory, "AgentDock.Engine.exe"), runner[0]);
    }

    [Fact]
    public void SameFolderStandaloneAppHostIsUsedWhenNoPackagedEngineExists()
    {
        var baseDirectory = Path.Combine(_root, "app-standalone");
        Directory.CreateDirectory(baseDirectory);
        File.WriteAllText(Path.Combine(baseDirectory, "AgentDock.Engine.exe"), "fixture");
        File.WriteAllText(Path.Combine(baseDirectory, "AgentDock.Engine.dll"), "fixture");

        var runner = ExecutionSetup.ResolveRunner(null, baseDirectory);

        Assert.Equal(Path.Combine(baseDirectory, "AgentDock.Engine.exe"), runner[0]);
    }

    [Fact]
    public void DevelopmentLayoutUsesTheDotnetHostAndTheEngineDll()
    {
        var baseDirectory = Path.Combine(_root, "app-dev");
        Directory.CreateDirectory(baseDirectory);
        var assembly = Path.Combine(baseDirectory, "AgentDock.Engine.dll");
        File.WriteAllText(assembly, "fixture");

        var runner = ExecutionSetup.ResolveRunner(null, baseDirectory);

        Assert.Equal(2, runner.Length);
        Assert.Contains("dotnet", runner[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(assembly, runner[1]);
    }

    [Fact]
    public void MissingRunnerLayoutFailsClosed()
    {
        var baseDirectory = Path.Combine(_root, "app-empty");
        Directory.CreateDirectory(baseDirectory);

        var error = Assert.Throws<DockException>(() => ExecutionSetup.ResolveRunner(null, baseDirectory));

        Assert.Equal("engine-runner-missing", error.Code);
    }
}
