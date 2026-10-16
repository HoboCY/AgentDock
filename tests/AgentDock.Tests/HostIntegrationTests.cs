using System.Diagnostics;
using System.Text.Json;
using AgentDock.Core;
using Xunit;
using TaskStatus = AgentDock.Core.TaskStatus;

namespace AgentDock.Tests;

public sealed class HostIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agentdock-host-" + Guid.NewGuid().ToString("N"));
    private Process? host;
    private DockClient Client => new(root);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsMacOS())
        {
            var launcher = new DockClient(root, Path.ChangeExtension(typeof(AgentDock.Host.Program).Assembly.Location, null));
            await launcher.EnsureHostAsync();
            host = Process.GetProcessById((await Client.InvokeAsync<HostStatus>("status")).ProcessId);
            return;
        }
        host = Start("--host", root);
        for (var i = 0; i < 40; i++)
        {
            if (host.HasExited) throw new Exception("Host exited: " + await host.StandardError.ReadToEndAsync());
            try { await Client.InvokeAsync<HostStatus>("status"); return; }
            catch (TimeoutException) { await Task.Delay(50); }
            catch (IOException) { await Task.Delay(50); }
        }
        throw new TimeoutException("Host did not become ready.");
    }

    [Fact]
    public async Task SequentialShortRequestsKeepTheHostReachable()
    {
        for (var index = 0; index < 100; index++)
            Assert.Equal(host!.Id, (await Client.InvokeAsync<HostStatus>("status")).ProcessId);
    }

    [Fact]
    public async Task DuplicateHostStartsReuseOneTaskOwnerAndRequireIndependentCredentials()
    {
        var first = await Client.InvokeAsync<HostStatus>("status");
        using var second = Start("--host", root);
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.ProcessId, (await Client.InvokeAsync<HostStatus>("status")).ProcessId);
        var config = await Client.InvokeAsync<ConfigurationView>("config.get");
        Assert.False(config.CredentialConfigured);
        var error = await Assert.ThrowsAsync<DockException>(() => Client.InvokeAsync<TaskSummary>("start",
            new TaskRequest("missing-credential", root, "probe", "explorer", "read")));
        Assert.Equal("CredentialRequired", error.Code);
    }

    [Fact]
    public async Task HostReturnsFailedExecutionWithoutRepeatingItAndDoesNotExposeCredential()
    {
        const string syntheticKey = "synthetic-agentdock-key-NOT-A-REAL-CREDENTIAL";
        var view = await Client.InvokeAsync<ConfigurationView>("credential.set", new { key = syntheticKey });
        Assert.True(view.CredentialConfigured);
        Assert.DoesNotContain(syntheticKey, JsonSerializer.Serialize(view));
        var request = new TaskRequest("failed-request", root, "probe", "explorer", "read");
        var task = await Client.InvokeAsync<TaskSummary>("start", request);
        TaskDetail detail;
        long after = 0;
        do
        {
            detail = await Client.InvokeAsync<TaskDetail>("wait", new { id = task.Id, after, waitSeconds = 5 });
            after = detail.NextCursor;
        } while (detail.Summary.Status.IsUnfinished());
        Assert.Equal(TaskStatus.Failed, detail.Summary.Status);
        Assert.Equal(task.Id, (await Client.InvokeAsync<TaskSummary>("start", request)).Id);
        Assert.DoesNotContain(syntheticKey, JsonSerializer.Serialize(detail));
        Assert.Single(await Client.InvokeAsync<TaskSummary[]>("list"));
    }

    [Fact]
    public async Task RealStdioMcpListsToolsAndCallsExistingBackground()
    {
        using var mcp = Start("--mcp", root);
        async Task<JsonElement> Request(int id, string method, object? parameters = null)
        {
            await mcp.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await mcp.StandardInput.FlushAsync();
            var line = await mcp.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return JsonDocument.Parse(line!).RootElement.Clone();
        }
        try
        {
            var initialized = await Request(1, "initialize");
            Assert.Equal("AgentDock", initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            var tools = await Request(2, "tools/list");
            Assert.Contains(tools.GetProperty("result").GetProperty("tools").EnumerateArray(), x => x.GetProperty("name").GetString() == "resume_task");
            var status = await Request(3, "tools/call", new { name = "status", arguments = new { } });
            Assert.False(status.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Equal(host!.Id, status.GetProperty("result").GetProperty("structuredContent").GetProperty("result").GetProperty("processId").GetInt32());
            mcp.StandardInput.Close();
            await mcp.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(host.HasExited);
        }
        finally { if (!mcp.HasExited) mcp.Kill(true); }
    }

    [Fact]
    public async Task DesktopNotifiesFailuresThatFinishBetweenPollsAndIncludesQueuedTime()
    {
        var state = new AgentDock.Desktop.AppState(new AgentDock.Desktop.HostApi(Client));
        var messages = new List<string>();
        state.NotifyRequested += (message, failure) => { if (failure) messages.Add(message); };
        await state.PollAsync();
        await Client.InvokeAsync<ConfigurationView>("credential.set", new { key = "synthetic-key-not-real" });
        var task = await Client.InvokeAsync<TaskSummary>("start", new TaskRequest("fast-failure", root, "fast failure", "explorer", "read"));
        long after = 0;
        TaskDetail detail;
        do
        {
            detail = await Client.InvokeAsync<TaskDetail>("wait", new { id = task.Id, after, waitSeconds = 5 });
            after = detail.NextCursor;
        } while (detail.Summary.Status.IsUnfinished());
        await state.PollAsync();
        Assert.Contains(messages, x => x.Contains("fast failure"));
        var accepted = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var queuedExample = task with { AcceptedAt = accepted, StartedAt = accepted.AddMinutes(2), FinishedAt = accepted.AddMinutes(3) };
        Assert.Equal("3:00", AgentDock.Desktop.Fmt.Elapsed(queuedExample, accepted.AddMinutes(10)));
    }

    [SandboxFact]
    public async Task RestrictedTaskCannotCallTheManagementPipeEvenWithTheConnectionToken()
    {
        var sandboxHome = Path.Combine(root, "sandbox-home");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(sandboxHome); Directory.CreateDirectory(runtime);
        var fs = "{\":root\"=\"read\"," + JsonSerializer.Serialize(runtime) + "=\"write\"}";
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX")!)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "sandbox", "-P", "probe", "-C", root, "-c", "permissions.probe.filesystem=" + fs,
            "-c", "permissions.probe.network.enabled=true", "-c", "windows.sandbox=\"unelevated\"", "--", "dotnet",
            typeof(RecoveryFixture).Assembly.Location, "--pipe-auth-probe", root }) start.ArgumentList.Add(arg);
        start.Environment["CODEX_HOME"] = sandboxHome;
        start.Environment["TEMP"] = runtime;
        start.Environment["TMP"] = runtime;
        start.Environment["DOTNET_CLI_HOME"] = runtime;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException)
            {
                process.Kill(true); await process.WaitForExitAsync();
                throw new Exception("Restricted client timeout. stdout=" + await output + " stderr=" + await error);
            }
            Assert.True(process.ExitCode == 0, await error);
            var observed = await output;
            Assert.Contains("PROBE_STARTED", observed);
            Assert.Contains("TOKEN_READ", observed);
            Assert.DoesNotContain("ACCEPTED", observed);
            Assert.True(observed.Contains("UntrustedClient") || observed.Contains("PIPE_DENIED:UnauthorizedAccessException"), observed);
            if (OperatingSystem.IsMacOS()) Assert.Contains("UntrustedClient", observed);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    private static Process Start(string mode, string home)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false, true) };
        start.ArgumentList.Add(typeof(AgentDock.Host.Program).Assembly.Location);
        start.ArgumentList.Add(mode); start.ArgumentList.Add("--home"); start.ArgumentList.Add(home);
        start.Environment["AGENTDOCK_HOST"] = Path.ChangeExtension(typeof(AgentDock.Host.Program).Assembly.Location,
            OperatingSystem.IsWindows() ? ".exe" : null);
        return Process.Start(start)!;
    }
    public async Task DisposeAsync()
    {
        if (host is not null)
        {
            if (!host.HasExited)
            {
                try { await Client.InvokeAsync<HostStatus>("shutdown", new { confirm = true }); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); } }
            }
            host.Dispose();
        }
        if (OperatingSystem.IsMacOS())
        {
            await MacHostLauncher.RemoveStoppedAsync(root);
            // Only this fixture's unique temporary data home can be removed.
            var identity = AgentDock.Engine.MacKeychain.Identity(root);
            Assert.StartsWith(ProjectPaths.Canonicalize(Path.GetTempPath()) + Path.DirectorySeparatorChar, identity.Account);
            AgentDock.Engine.MacKeychain.Delete(identity);
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

public sealed class SandboxFactAttribute : FactAttribute
{
    public SandboxFactAttribute()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            !File.Exists(Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX")))
            Skip = "Requires the pinned platform sandbox executable via AGENTDOCK_TEST_SANDBOX.";
    }
}
