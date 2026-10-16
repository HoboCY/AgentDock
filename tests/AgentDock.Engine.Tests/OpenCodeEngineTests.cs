using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class OpenCodeEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdock-engine-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _project;
    private readonly string _enginePath;
    private readonly string _sandboxPath;
    private readonly string _shellPath;
    private readonly string _sandboxBin;

    public OpenCodeEngineTests()
    {
        _home = Path.Combine(_root, "home");
        _project = Path.Combine(_root, "project");
        _enginePath = Path.Combine(_root, "tools", "opencode.exe");
        _sandboxBin = Path.Combine(_root, "tools", "codex", "bin");
        _sandboxPath = Path.Combine(_sandboxBin, "codex.exe");
        _shellPath = Path.Combine(_root, "tools", "bash.exe");
        foreach (var file in new[] { _enginePath, _sandboxPath, _shellPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }
        Directory.CreateDirectory(Path.Combine(_root, "tools", "codex", "codex-path"));
        File.WriteAllText(Path.Combine(_root, "tools", "codex", "codex-path", OperatingSystem.IsWindows() ? "rg.exe" : "rg"), "fixture");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_home);
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

    private ExecutionTask TaskFor(string role = "worker", string prompt = "Do the assigned work.")
    {
        var settings = new DockConfiguration
        {
            EnginePath = _enginePath,
            SandboxPath = _sandboxPath,
            ShellPath = _shellPath,
        };
        return new ExecutionTask("task-1", _project, role, prompt, settings.Roles[role], settings);
    }

    private static EngineHooks Hooks(Func<string, string>? versions = null) => new()
    {
        VersionProbe = (path, _) => Task.FromResult(versions is null ? ExpectedFor(path) : versions(path)),
    };

    private static string ExpectedFor(string path) =>
        path.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase) ? "codex-cli 0.153.4" : "1.18.30";

    private OpenCodeEngine Engine(EngineHooks? hooks = null, Func<string?>? credential = null) =>
        new(_home, credential ?? (() => "fixture-credential-not-real"), hooks ?? Hooks());

    [Fact]
    public async Task PreCancelledCallReturnsCancelledWithoutRunningAnything()
    {
        var engine = Engine();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var result = await engine.ExecuteAsync(TaskFor(), _ => { }, source.Token);
        Assert.False(result.Success);
        Assert.Contains("Cancelled", result.Error);
        Assert.Empty(result.Changes ?? []);
    }

    [Fact]
    public async Task MissingCredentialFailsClosed()
    {
        var engine = Engine(credential: () => null);
        var error = await Assert.ThrowsAsync<DockException>(() =>
            engine.ExecuteAsync(TaskFor(), _ => { }, CancellationToken.None));
        Assert.Equal("credential-missing", error.Code);
    }

    [Fact]
    public async Task InvalidCredentialFailsClosed()
    {
        var engine = Engine(credential: () => "not valid with spaces");
        var error = await Assert.ThrowsAsync<DockException>(() =>
            engine.ExecuteAsync(TaskFor(), _ => { }, CancellationToken.None));
        Assert.Equal("credential-invalid", error.Code);
    }

    [Fact]
    public async Task MissingOrRelativeEnginePathFailsClosed()
    {
        var missing = TaskFor() with
        {
            Settings = TaskFor().Settings with { EnginePath = Path.Combine(_root, "nope", "opencode.exe") },
        };
        var error = await Assert.ThrowsAsync<DockException>(() =>
            Engine().ExecuteAsync(missing, _ => { }, CancellationToken.None));
        Assert.Equal("engine-missing", error.Code);

        var relative = TaskFor() with
        {
            Settings = TaskFor().Settings with { EnginePath = "tools/opencode.exe" },
        };
        var relativeError = await Assert.ThrowsAsync<DockException>(() =>
            Engine().ExecuteAsync(relative, _ => { }, CancellationToken.None));
        Assert.Equal("engine-missing", relativeError.Code);
    }

    [Fact]
    public async Task VersionMismatchFailsClosed()
    {
        var engine = Engine(Hooks(path => path.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase)
            ? "codex-cli 0.153.4"
            : "2.0.0"));
        var error = await Assert.ThrowsAsync<DockException>(() =>
            engine.ExecuteAsync(TaskFor(), _ => { }, CancellationToken.None));
        Assert.Equal("engine-version", error.Code);
    }

    [Fact]
    public async Task SandboxVersionMismatchFailsClosed()
    {
        var engine = Engine(Hooks(path => path.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase)
            ? "codex-cli 9.9.9"
            : "1.18.30"));
        var error = await Assert.ThrowsAsync<DockException>(() =>
            engine.ExecuteAsync(TaskFor(), _ => { }, CancellationToken.None));
        Assert.Equal("sandbox-version", error.Code);
    }

    [Fact]
    public async Task OversizedPromptFailsClosed()
    {
        var engine = Engine();
        var task = TaskFor(prompt: new string('x', 1024 * 1024 + 1));
        var error = await Assert.ThrowsAsync<DockException>(() =>
            engine.ExecuteAsync(task, _ => { }, CancellationToken.None));
        Assert.Equal("prompt-invalid", error.Code);
    }

    [Fact]
    public async Task CancellationDuringVersionProbeReturnsCancelledResult()
    {
        var engine = Engine(new EngineHooks
        {
            VersionProbe = (_, _) => throw new OperationCanceledException(),
        });
        var result = await engine.ExecuteAsync(TaskFor(), _ => { }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Cancelled", result.Error);
    }

    [Fact]
    public async Task MissingShellFailsClosed()
    {
        var task = TaskFor() with
        {
            Settings = TaskFor().Settings with { ShellPath = "" },
        };
        var error = await Assert.ThrowsAsync<DockException>(() =>
            Engine().ExecuteAsync(task, _ => { }, CancellationToken.None));
        Assert.Equal("shell-missing", error.Code);
    }

    [Fact]
    public async Task MissingProjectFailsClosed()
    {
        var settings = TaskFor().Settings;
        var task = new ExecutionTask("task-1", Path.Combine(_root, "missing-project"), "worker", "Do work.",
            settings.Roles["worker"], settings);
        var error = await Assert.ThrowsAsync<DockException>(() =>
            Engine().ExecuteAsync(task, _ => { }, CancellationToken.None));
        Assert.Equal("project-missing", error.Code);
    }

    [Fact]
    public void ScrubRedactsCredentialAndCapabilityTokens()
    {
        var scrubbed = OpenCodeEngine.Scrub("value sk-secret and cap-token-123 plus sk-secret again",
            "sk-secret", "cap-token-123");

        Assert.DoesNotContain("sk-secret", scrubbed);
        Assert.DoesNotContain("cap-token-123", scrubbed);
        Assert.Contains("[redacted]", scrubbed);
    }
}
