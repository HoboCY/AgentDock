using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentDock.Host;
using AgentDock.Core;
using Xunit;

namespace AgentDock.Tests;

public sealed class DockClientTransportTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "agentdock-transport-" + Guid.NewGuid().ToString("N"));

    public DockClientTransportTests()
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "connection.token"), "synthetic-transport-token");
    }

    [WindowsPipeFact]
    public async Task BrieflyBusyPipeDoesNotFailOrRepeatTheRequest()
    {
        // Occupy the only server instance, as happens while the listener is delayed under load.
        using var server = new NamedPipeServerStream(DockClient.PipeFor(home), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var occupant = new NamedPipeClientStream(".", DockClient.PipeFor(home), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var accepted = server.WaitForConnectionAsync(deadline.Token);
        await occupant.ConnectAsync(deadline.Token);
        await accepted;
        var invoke = new DockClient(home).InvokeAsync<int>("start", new { request_id = "busy-pipe-once" }, deadline.Token);
        await Task.Delay(1100, deadline.Token);
        server.Disconnect();
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var request = await reader.ReadLineAsync(deadline.Token);
            Assert.Contains("busy-pipe-once", request);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("{\"result\":1}");
        }, deadline.Token);
        try { Assert.Equal(1, await invoke); await serve; }
        finally { await deadline.CancelAsync(); try { await serve; } catch (OperationCanceledException) { } }
    }

    [WindowsPipeFact]
    public async Task CancellationStopsWaitingForAConnectionPromptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockClient(home).InvokeAsync<int>("status", cancellationToken: cancellation.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }

    [WindowsPipeFact]
    public async Task DisconnectionAfterReceivingARequestDoesNotReplayIt()
    {
        using var server = new NamedPipeServerStream(DockClient.PipeFor(home), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var invoke = new DockClient(home).InvokeAsync<int>("stop", new { id = "synthetic-task" }, deadline.Token);
        await server.WaitForConnectionAsync(deadline.Token);
        using (var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true))
            Assert.Contains("synthetic-task", await reader.ReadLineAsync(deadline.Token));
        server.Disconnect();
        var next = server.WaitForConnectionAsync(deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => invoke);
        await Task.Delay(200, deadline.Token);
        Assert.False(next.IsCompleted, "Client replayed a request after a response was lost.");
        await deadline.CancelAsync();
        try { await next; } catch (OperationCanceledException) { }
    }

    [WindowsPipeTheory]
    [InlineData(false, "HostConnectionTimeout")]
    [InlineData(true, "HostTransportError")]
    public async Task McpDistinguishesConnectionTimeoutFromLostResponse(bool acceptBusinessRequest, string expectedCode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var status = new NamedPipeServerStream(DockClient.PipeFor(home), PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var output = new StringWriter();
        var rpc = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}\n";
        var mcp = new McpServer(new DockClient(home)).RunAsync(new StringReader(rpc), output, deadline.Token);
        await status.WaitForConnectionAsync(deadline.Token);
        using (var reader = new StreamReader(status, Encoding.UTF8, leaveOpen: true))
            Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
        using var business = acceptBusinessRequest ? new NamedPipeServerStream(DockClient.PipeFor(home), PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly) : null;
        using (var writer = new StreamWriter(status, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
            await writer.WriteLineAsync("{\"result\":{\"version\":\"test\",\"processId\":123,\"unfinishedTasks\":0,\"canUpdate\":true}}");
        // The first instance stays occupied: the second connection can only use 'business'.
        if (business is not null)
        {
            await business.WaitForConnectionAsync(deadline.Token);
            using (var reader = new StreamReader(business, Encoding.UTF8, leaveOpen: true))
                Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
            business.Disconnect();
        }
        await mcp;
        using var response = JsonDocument.Parse(output.ToString());
        var result = response.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        using var error = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(expectedCode, error.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-transport-token", output.ToString());
        Assert.DoesNotContain(home, output.ToString());
    }

    public void Dispose() => Directory.Delete(home, true);
}

public sealed class WindowsPipeFactAttribute : FactAttribute
{
    public WindowsPipeFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows named-pipe acceptance regression.";
    }
}

public sealed class WindowsPipeTheoryAttribute : TheoryAttribute
{
    public WindowsPipeTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows named-pipe acceptance regression.";
    }
}
