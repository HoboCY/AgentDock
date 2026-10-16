using System.Diagnostics;
using System.Text;
using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class MacExecutionFactAttribute : FactAttribute
{
    public MacExecutionFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("AGENTDOCK_MAC_EXECUTION_TESTS") != "1")
            Skip = "Requires an authorized Mac GUI session and AGENTDOCK_MAC_EXECUTION_TESTS=1.";
    }
}

public sealed class MacExecutionTheoryAttribute : TheoryAttribute
{
    public MacExecutionTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("AGENTDOCK_MAC_EXECUTION_TESTS") != "1")
            Skip = "Requires an authorized Mac GUI session and AGENTDOCK_MAC_EXECUTION_TESTS=1.";
    }
}

/// <summary>Real macOS containment and pinned runtime; only synthetic upstream replies.</summary>
public sealed class MacExecutionTests : IDisposable
{
    private readonly string _root = ProjectPaths.Canonicalize(Path.Combine(Path.GetTempPath(), "agentdock-mac-execution-" + Guid.NewGuid().ToString("N")));

    public MacExecutionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS()) MacJobProcess.RecoverAbandonedJobs(_root);
        Directory.Delete(_root, true);
    }

    [MacExecutionFact]
    public void JobRelaysStdioAndReturnsOnlyAfterItsCoalitionStops()
    {
        using var job = MacJobProcess.Start("/bin/sh", ["-c", "cat; printf 'ERR_OK' >&2"], _root,
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["AGENTDOCK_MAC_JOB_ROOT"] = Path.Combine(_root, "engine", "direct-12345678") });
        var stdout = Task.Run(() => new StreamReader(job.Stdout).ReadToEnd());
        var stderr = Task.Run(() => new StreamReader(job.Stderr).ReadToEnd());
        job.Stdin.Write(Encoding.UTF8.GetBytes("PROMPT_OK"));
        job.Stdin.Dispose();
        Assert.True(job.WaitForExit(45_000));
        Assert.Equal(0, job.ExitCode);
        Assert.Contains("PROMPT_OK", stdout.GetAwaiter().GetResult());
        Assert.Contains("ERR_OK", stderr.GetAwaiter().GetResult());
    }

    [MacExecutionTheory]
    [InlineData("default", true)]
    [InlineData("worker", true)]
    [InlineData("explorer", false)]
    [InlineData("reviewer", false)]
    [InlineData("verifier", false)]
    public async Task PinnedPipelineEnforcesRoleWritesAndReportsUsage(string role, bool writable)
    {
        var project = Path.Combine(_root, "project");
        var neighbor = Path.Combine(_root, "neighbor");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(neighbor);
        Directory.CreateSymbolicLink(Path.Combine(project, "outside-link"), neighbor);
        var settings = Settings();
        var task = new ExecutionTask("mac-fixture", project, role, "Run the fixture checks.", settings.Roles[role], settings);
        var script = $"if printf x > '{project}/receipt.txt'; then echo PROJECT_WRITE; else echo PROJECT_DENIED; fi; " +
            $"if printf x > '{neighbor}/forbidden.txt'; then echo OUTSIDE_WRITE; else echo OUTSIDE_DENIED; fi; " +
            $"if printf x > '{project}/outside-link/escaped.txt'; then echo LINK_WRITE; else echo LINK_DENIED; fi; " +
            "if printf x > \"$HOME/../check-artifacts/check.txt\"; then echo ARTIFACT_OK; fi; " +
            "if [ -z \"$OPENAI_API_KEY$DEEPSEEK_API_KEY\" ]; then echo NO_INHERITED_KEY; fi";
        using var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = script, description = "Check synthetic Mac fixture permissions" }),
            ScriptedModel.Text("MAC_FIXTURE_OK")));
        var events = new List<EngineEvent>();
        var engine = new OpenCodeEngine(_root, () => "synthetic-key-not-real", new EngineHooks { RelayTransport = handler });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var result = await engine.ExecuteAsync(task, events.Add, timeout.Token);
        Assert.True(result.Success, result.Error);
        Assert.Contains("MAC_FIXTURE_OK", result.Result);
        var output = string.Join('\n', events.Where(e => e.Kind == "tool").Select(e => e.Text));
        Assert.Contains(writable ? "PROJECT_WRITE" : "PROJECT_DENIED", output);
        Assert.Contains("OUTSIDE_DENIED", output);
        Assert.Contains("LINK_DENIED", output);
        Assert.Contains("ARTIFACT_OK", output);
        Assert.Contains("NO_INHERITED_KEY", output);
        Assert.Equal(writable, File.Exists(Path.Combine(project, "receipt.txt")));
        Assert.False(File.Exists(Path.Combine(neighbor, "forbidden.txt")));
        Assert.False(File.Exists(Path.Combine(neighbor, "escaped.txt")));
        Assert.Contains(events, e => e.Kind == "usage" && e.InputTokens > 0);
        Assert.Equal(2, handler.Requests);
    }

    private static DockConfiguration Settings() => new()
    {
        EnginePath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_OPENCODE")!,
        SandboxPath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX")!,
        ShellPath = "/bin/bash",
    };

    [MacExecutionTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndTimeoutKillDetachedToolDescendants(bool timeout)
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var settings = Settings();
        var role = settings.Roles["worker"] with { TimeoutSeconds = timeout ? 15 : 90 };
        var task = new ExecutionTask("stop-fixture", project, "worker", "Run fixture.", role, settings);
        using var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = DetachedCommand(project, true), description = "Detach a bounded fixture", timeout = 120000 }),
            ScriptedModel.Text("UNEXPECTED_COMPLETION")));
        var engine = new OpenCodeEngine(_root, () => "synthetic-key-not-real", new EngineHooks { RelayTransport = handler });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        var execution = engine.ExecuteAsync(task, _ => { }, cancel.Token);
        var identity = await WaitForDetached(project);
        if (!timeout) cancel.Cancel();
        var result = await execution;
        Assert.False(result.Success);
        Assert.Contains(timeout ? "Timed out" : "Cancelled", result.Error);
        Assert.Equal(MacProbeStatus.Vanished, MacProcessProbe.CheckAlive(identity, out _));
        Assert.Contains(result.Changes!, change => change.Path == "detached.pid");
        await AssertMarkerStopped(project);
    }

    [MacExecutionFact]
    public async Task NormalCompletionAlsoKillsDetachedDescendants()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var settings = Settings();
        var task = new ExecutionTask("normal-detach", project, "worker", "Run fixture.", settings.Roles["worker"], settings);
        using var handler = new ScriptedHandler(new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = DetachedCommand(project, false), description = "Detach bounded fixture" }),
            ScriptedModel.Text("DONE")));
        var engine = new OpenCodeEngine(_root, () => "synthetic-key-not-real", new EngineHooks { RelayTransport = handler });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        var execution = engine.ExecuteAsync(task, _ => { }, cancel.Token);
        var identity = await WaitForDetached(project);
        var result = await execution;
        Assert.True(result.Success, result.Error);
        Assert.Equal(MacProbeStatus.Vanished, MacProcessProbe.CheckAlive(identity, out _));
        await AssertMarkerStopped(project);
    }

    [MacExecutionFact]
    public async Task HostProcessDeathClosesLeaseAndKillsDetachedDescendants()
    {
        var start = new ProcessStartInfo(ExecutionSetup.FindDotnet()!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(MacCrashFixture).Assembly.Location);
        start.ArgumentList.Add("--mac-crash-fixture");
        start.ArgumentList.Add(_root);
        using var host = Process.Start(start)!;
        var stdout = host.StandardOutput.ReadToEndAsync();
        var stderr = host.StandardError.ReadToEndAsync();
        try
        {
            var project = Path.Combine(_root, "project");
            var identity = await WaitForDetached(project);
            host.Kill();
            await host.WaitForExitAsync();
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (MacProcessProbe.CheckAlive(identity, out _) == MacProbeStatus.Found && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.Equal(MacProbeStatus.Vanished, MacProcessProbe.CheckAlive(identity, out _));
            await AssertMarkerStopped(project);
        }
        finally
        {
            if (!host.HasExited) host.Kill();
            await host.WaitForExitAsync();
            _ = await stdout;
            _ = await stderr;
        }
    }

    // Test-only Python supplied by the validation Mac's CLT; never an application dependency.
    internal static string DetachedCommand(string project, bool keepParent) => "/usr/bin/python3 -c " + Quote("""
        import os,time
        if os.fork():
            os.wait()
            raise SystemExit(0)
        os.setsid()
        if os.fork(): raise SystemExit(0)
        for fd in (0,1,2): os.close(fd)
        with open('detached.pid','w') as f: f.write(str(os.getpid()))
        deadline=time.monotonic()+60
        while time.monotonic()<deadline:
            with open('heartbeat.txt','a') as f: f.write('x')
            time.sleep(.1)
        """) + (keepParent ? "; sleep 60" : "");

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static async Task<MacProcessIdentity> WaitForDetached(string project)
    {
        var pidFile = Path.Combine(project, "detached.pid");
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid) &&
                MacProcessProbe.TryGetIdentity(pid, out var identity, out _) == MacProbeStatus.Found) return identity;
            await Task.Delay(50);
        }
        throw new TimeoutException("The synthetic detached tool did not register.");
    }

    private static async Task AssertMarkerStopped(string project)
    {
        var path = Path.Combine(project, "heartbeat.txt");
        var length = new FileInfo(path).Length;
        await Task.Delay(500);
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [MacExecutionFact]
    public void UsedControlDirectoryCannotReleaseAnotherTask()
    {
        var root = Path.Combine(_root, "engine", "reused-12345678");
        var environment = new Dictionary<string, string> { ["PATH"] = "/bin:/usr/bin", ["AGENTDOCK_MAC_JOB_ROOT"] = root };
        using (var first = MacJobProcess.Start("/bin/sh", ["-c", "exit 0"], _root, environment))
        {
            first.Stdin.Dispose();
            Assert.True(first.WaitForExit(30_000));
            Assert.Equal(0, first.ExitCode);
        }
        var error = Assert.Throws<DockException>(() => MacJobProcess.Start("/bin/sh", ["-c", "exit 12"], _root, environment));
        Assert.Equal("mac-job-root-used", error.Code);
    }

    [MacExecutionFact]
    public void SameNamedDirectoriesHaveIndependentLaunchdJobs()
    {
        var rootA = Path.Combine(_root, "engine", "same-12345678");
        var rootB = Path.Combine(_root, "other", "engine", "same-12345678");
        using var first = MacJobProcess.Start("/bin/sh", ["-c", "sleep 45"], _root,
            new Dictionary<string, string> { ["PATH"] = "/bin:/usr/bin", ["AGENTDOCK_MAC_JOB_ROOT"] = rootA });
        using var second = MacJobProcess.Start("/bin/sh", ["-c", "sleep 45"], _root,
            new Dictionary<string, string> { ["PATH"] = "/bin:/usr/bin", ["AGENTDOCK_MAC_JOB_ROOT"] = rootB });
        first.Stdin.Dispose(); second.Stdin.Dispose();
        Assert.NotEqual(MacJobControl.LabelFor(rootA), MacJobControl.LabelFor(rootB));
        Thread.Sleep(1_500);
        first.Terminate();
        Assert.False(second.HasExited);
        second.Terminate();
    }

    [MacExecutionFact]
    public async Task WatchdogDeathFailsTaskAndParentCleansTheCoalition()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        using var job = MacJobProcess.Start("/bin/bash", ["-c", DetachedCommand(project, true)], project,
            new Dictionary<string, string> { ["PATH"] = "/bin:/usr/bin", ["AGENTDOCK_MAC_JOB_ROOT"] = Path.Combine(_root, "engine", "watchdog-12345678") });
        var stdout = Task.Run(() => job.Stdout.CopyTo(Stream.Null));
        var stderr = Task.Run(() => job.Stderr.CopyTo(Stream.Null));
        job.Stdin.Dispose();
        var identity = await WaitForDetached(project);
        Assert.Equal(0, MacProcessProbe.Signal(job.WatchdogIdentity, MacProcessProbe.SignalKill));
        Assert.True(job.WaitForExit(30_000));
        Assert.NotEqual(0, job.ExitCode);
        Assert.Equal(MacProbeStatus.Vanished, MacProcessProbe.CheckAlive(identity, out _));
        await Task.WhenAll(stdout, stderr);
        await AssertMarkerStopped(project);
    }

    [MacExecutionFact]
    public void RecoveryNeverSignalsIdentityFromAPreviousBoot()
    {
        var root = Path.Combine(_root, "engine", "oldboot-12345678");
        using (var job = MacJobProcess.Start("/bin/sh", ["-c", "exit 0"], _root,
            new Dictionary<string, string> { ["AGENTDOCK_MAC_JOB_ROOT"] = root }))
        {
            job.Stdin.Dispose();
            Assert.True(job.WaitForExit(30_000));
        }
        var currentIdentity = MacProcessProbe.GetCurrent();
        var configPath = MacJobControl.ConfigPath(root);
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!;
        config["bootSession"] = Guid.NewGuid().ToString("D");
        File.WriteAllText(configPath, config.ToJsonString());
        MacJobControl.WriteAtomic(MacJobControl.IdentityPath(root), MacJobControl.BuildIdentityJson(currentIdentity));
        MacJobProcess.RecoverAbandonedJobs(_root);
        Assert.Equal(MacProbeStatus.Found, MacProcessProbe.CheckAlive(currentIdentity, out _));
    }

    [MacExecutionFact]
    public async Task ExcessiveOutputStopsTheJobWithFailure()
    {
        using var job = MacJobProcess.Start("/usr/bin/yes", [], _root,
            new Dictionary<string, string> { ["AGENTDOCK_MAC_JOB_ROOT"] = Path.Combine(_root, "engine", "output-12345678") });
        var stdout = Task.Run(() => job.Stdout.CopyTo(Stream.Null));
        var stderr = Task.Run(() => new StreamReader(job.Stderr).ReadToEnd());
        job.Stdin.Dispose();
        Assert.True(job.WaitForExit(30_000));
        Assert.NotEqual(0, job.ExitCode);
        Assert.Contains("disk budget", await stderr);
        await stdout;
        Assert.InRange(new FileInfo(Path.Combine(_root, "engine", "output-12345678", "control", "job.stdout")).Length,
            1, MacJobControl.LogLimitBytes);
    }
}
