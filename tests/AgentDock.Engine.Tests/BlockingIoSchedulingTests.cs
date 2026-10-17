using System.Diagnostics;
using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

/// <summary>Windows-only gate for the blocking-I/O fixture child.</summary>
public sealed class WindowsBlockingIoFactAttribute : FactAttribute
{
    public WindowsBlockingIoFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows; the fixture child exercises the Windows OpenCodeEngine platform path.";
        }
    }
}

/// <summary>
/// Regression for shared thread-pool starvation under blocking child I/O: four
/// concurrent jobs that block inside stream reads/writes and WaitForExit must
/// not stop already-queued work from running. The scenario runs in an isolated
/// child process, so its ThreadPool limits never touch the xunit host pool.
/// </summary>
public sealed class BlockingIoSchedulingTests
{
    private const int ChildTimeoutMs = 75_000;

    [WindowsBlockingIoFact]
    public async Task SharedThreadPoolStaysResponsiveWhileFourJobsBlockOnSynchronousIo()
    {
        var root = Path.Combine(Path.GetTempPath(), BlockingIoFixture.RootPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dotnet = ExecutionSetup.FindDotnet()
                ?? throw new InvalidOperationException("No dotnet host was found to run the fixture child.");
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(typeof(MacCrashFixture).Assembly.Location);
            start.ArgumentList.Add(BlockingIoFixture.Argument);
            start.ArgumentList.Add(root);
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            var exited = child.WaitForExit(ChildTimeoutMs);
            if (!exited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the timeout and the kill.
                }
                child.WaitForExit(10_000);
            }
            var output = (await stdout) + (await stderr);
            Assert.True(exited,
                $"The blocking-I/O fixture child did not exit within {ChildTimeoutMs} ms." + Environment.NewLine + output);
            Assert.True(child.ExitCode == 0,
                $"The fixture child failed with exit code {child.ExitCode}: a queued heartbeat could not run while four jobs blocked." +
                Environment.NewLine + output);
        }
        finally
        {
            BlockingIoFixture.TryDeleteRoot(root);
        }
    }
}

/// <summary>
/// Child-side fixture for <see cref="BlockingIoSchedulingTests"/>. Bounds its
/// own ThreadPool, runs four real ExecuteAsync calls against fake jobs whose
/// stdio and exit wait block on one shared gate, and reports whether an
/// already-queued work item could run while all worker threads were blocked.
/// </summary>
internal static class BlockingIoFixture
{
    public const string Argument = "--blocking-io-fixture";
    public const string RootPrefix = "agentdock-blocking-io-";

    private const int Jobs = 4;
    private const int Workers = 8;
    private const string Credential = "synthetic-blocking-io-credential";
    private const int HeartbeatDeadlineMs = 2_000;
    private const int SaturationDeadlineMs = 15_000;
    private const int DrainDeadlineMs = 20_000;
    private const int WatchdogMs = 70_000;

    public static int Run(string root)
    {
        var full = Path.GetFullPath(root);
        if (!IsFixtureRoot(full))
        {
            Console.Error.WriteLine("blocking-io-fixture: refusing a fixture root outside the temp directory.");
            return 3;
        }
        try
        {
            return RunCore(full);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("blocking-io-fixture: " + error.GetType().Name + ": " + error.Message);
            return 3;
        }
        finally
        {
            TryDeleteRoot(full);
        }
    }

    public static bool TryDeleteRoot(string root)
    {
        if (!IsFixtureRoot(root))
        {
            return false;
        }
        var full = Path.GetFullPath(root);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(full))
                {
                    Directory.Delete(full, recursive: true);
                }
                return !Directory.Exists(full);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
        return !Directory.Exists(full);
    }

    private static bool IsFixtureRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
            leaf.StartsWith(RootPrefix, StringComparison.Ordinal);
    }

    private static int RunCore(string root)
    {
        // Bound only the child's pool: the heartbeat below must stay pending
        // once every worker thread is blocked in a synchronous job call.
        ThreadPool.GetMinThreads(out _, out var minIo);
        ThreadPool.GetMaxThreads(out _, out var maxIo);
        if (!ThreadPool.SetMinThreads(Workers, minIo) || !ThreadPool.SetMaxThreads(Workers, maxIo))
        {
            Console.Error.WriteLine($"blocking-io-fixture: could not bound the child thread pool to {Workers} workers.");
            return 3;
        }

        var tools = Path.Combine(root, "tools");
        var enginePath = Path.Combine(tools, "opencode.exe");
        var sandboxPath = Path.Combine(tools, "codex", "bin", "codex.exe");
        var shellPath = Path.Combine(tools, "bash.exe");
        var runnerPath = Path.Combine(tools, "runner.exe");
        var project = Path.Combine(root, "project");
        foreach (var file in new[] { enginePath, sandboxPath, shellPath, runnerPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }
        var codexPath = Path.Combine(tools, "codex", "codex-path");
        Directory.CreateDirectory(codexPath);
        File.WriteAllText(Path.Combine(codexPath, "rg.exe"), "fixture");
        Directory.CreateDirectory(project);

        var release = new ManualResetEventSlim(false);
        var watchdogStop = new ManualResetEventSlim(false);
        var entered = 0;
        var started = 0;
        var watchdog = new Thread(() =>
        {
            if (!watchdogStop.Wait(WatchdogMs))
            {
                release.Set();
            }
        })
        {
            IsBackground = true,
            Name = "blocking-io-fixture-watchdog",
        };
        watchdog.Start();

        try
        {
            var settings = new DockConfiguration
            {
                EnginePath = enginePath,
                SandboxPath = sandboxPath,
                ShellPath = shellPath,
            };
            var hooks = new EngineHooks
            {
                VersionProbe = (path, _) => Task.FromResult(
                    path.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase)
                        ? OpenCodeEngine.RequiredSandboxOutput
                        : OpenCodeEngine.RequiredOpenCodeVersion),
                RunnerExecutable = runnerPath,
                ProcessStarter = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref started);
                    return new BlockingJob(release, () => Interlocked.Increment(ref entered));
                },
            };
            var executions = new Task<ExecutionResult>[Jobs];
            for (var index = 0; index < Jobs; index++)
            {
                var home = Path.Combine(root, "home-" + index);
                Directory.CreateDirectory(home);
                var task = new ExecutionTask("blocking-io-" + index, project, "worker", "Run the fixture.",
                    settings.Roles["worker"], settings);
                var engine = new OpenCodeEngine(home, () => Credential, hooks);
                executions[index] = engine.ExecuteAsync(task, _ => { }, CancellationToken.None);
            }

            var deadline = Environment.TickCount64 + SaturationDeadlineMs;
            while (Volatile.Read(ref entered) < Workers && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(25);
            }
            var saturation = Volatile.Read(ref entered);
            var heartbeat = new ManualResetEventSlim(false);
            ThreadPool.QueueUserWorkItem(_ => heartbeat.Set());
            var heartbeatObserved = heartbeat.Wait(HeartbeatDeadlineMs);

            int result;
            if (heartbeatObserved)
            {
                var startedDeadline = Environment.TickCount64 + 2_000;
                while (Volatile.Read(ref started) < Jobs && Environment.TickCount64 < startedDeadline)
                {
                    Thread.Sleep(25);
                }
                var startedCount = Volatile.Read(ref started);
                if (startedCount == Jobs && saturation >= Workers)
                {
                    Console.WriteLine(
                        $"blocking-io-fixture: heartbeat observed while {Jobs} jobs were blocked (entered={saturation} started={startedCount}).");
                    result = 0;
                }
                else
                {
                    Console.Error.WriteLine(
                        $"blocking-io-fixture: heartbeat passed but the scenario was not exercised (started={startedCount} entered={saturation}).");
                    result = 3;
                }
            }
            else
            {
                Console.Error.WriteLine(
                    $"blocking-io-fixture: shared thread pool did not run a queued work item within {HeartbeatDeadlineMs} ms while " +
                    $"{Jobs} concurrent jobs blocked on synchronous stdio and WaitForExit (entered={saturation} started={Volatile.Read(ref started)}).");
                result = 2;
            }

            release.Set();
            if (!Task.WhenAll(executions).Wait(DrainDeadlineMs))
            {
                Console.Error.WriteLine("blocking-io-fixture: blocking jobs did not drain within the deadline.");
                result = 3;
            }
            return result;
        }
        finally
        {
            release.Set();
            watchdogStop.Set();
            watchdog.Join(5_000);
        }
    }

    /// <summary>Fake contained job: every stdio call and the exit wait block on one gate.</summary>
    private sealed class BlockingJob : IJobProcess
    {
        private readonly ManualResetEventSlim _release;
        private readonly Action _blocked;
        private readonly BlockingStream _stdin;
        private readonly BlockingStream _stdout;
        private readonly BlockingStream _stderr;
        private int _exited;

        public BlockingJob(ManualResetEventSlim release, Action blocked)
        {
            _release = release;
            _blocked = blocked;
            _stdin = new BlockingStream(release, blocked, canWrite: true);
            _stdout = new BlockingStream(release, blocked, canWrite: false);
            _stderr = new BlockingStream(release, blocked, canWrite: false);
        }

        public Stream Stdin => _stdin;

        public Stream Stdout => _stdout;

        public Stream Stderr => _stderr;

        public bool HasExited => Volatile.Read(ref _exited) != 0;

        public int ExitCode => 0;

        public bool WaitForExit(int milliseconds)
        {
            _blocked();
            _release.Wait();
            Volatile.Write(ref _exited, 1);
            return true;
        }

        public void Terminate()
        {
            Volatile.Write(ref _exited, 1);
            _release.Wait();
        }

        public void Dispose()
        {
            _stdin.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
        }
    }

    /// <summary>Stream whose first read/write blocks on the shared release gate, then reports end-of-stream.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly ManualResetEventSlim _release;
        private readonly Action _blocked;
        private readonly bool _canWrite;
        private int _blockedOnce;

        public BlockingStream(ManualResetEventSlim release, Action blocked, bool canWrite)
        {
            _release = release;
            _blocked = blocked;
            _canWrite = canWrite;
        }

        public override bool CanRead => !_canWrite;

        public override bool CanSeek => false;

        public override bool CanWrite => _canWrite;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Block();
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count) => Block();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Block()
        {
            if (Interlocked.Exchange(ref _blockedOnce, 1) == 0)
            {
                _blocked();
            }
            _release.Wait();
        }
    }
}
