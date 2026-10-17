using System.IO.Pipes;
using AgentDock.Host;
using Xunit;

namespace AgentDock.Tests;

public sealed class PipeAcceptTests
{
    [WindowsPipeFact]
    public async Task ABlockingRequestDoesNotStopAcceptingOtherConnections()
    {
        var pipeName = "agentdock-accept-" + Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sequence = 0;
        var serving = Program.AcceptClientsAsync(pipeName, pipe =>
        {
            using (pipe)
            {
                if (Interlocked.Increment(ref sequence) == 1)
                {
                    entered.Set();
                    // A request may enter synchronous persistence/lock work before its first incomplete await.
                    release.Wait(cancellation.Token);
                }
                return Task.CompletedTask;
            }
        }, cancellation.Token);
        try
        {
            using var first = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await first.ConnectAsync(2000, cancellation.Token);
            Assert.True(await Task.Run(() => entered.Wait(2000)), "The blocking request did not start.");
            // One precreated listener can admit a second client without running the accept loop.
            // A third proves the loop actually continued while the first handler remained blocked.
            using var second = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var third = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await second.ConnectAsync(2000, cancellation.Token);
            await third.ConnectAsync(2000, cancellation.Token);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
            await cancellation.CancelAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
