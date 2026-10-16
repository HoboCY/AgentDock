using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// The two Engine helper roles the host-side <see cref="MacJobProcess"/> drives:
/// <list type="bullet">
/// <item><c>--mac-watchdog &lt;control.json&gt;</c>: holds the host lifetime
/// lease on stdin, bootstraps exactly one temporary launchd job in the
/// <c>gui/&lt;uid&gt;</c> domain (each bootstrap yields an independent resource
/// coalition), gates the job behind a protected go file only while the lease is
/// alive, pumps the job's file-backed stdio, and on EOF, cancellation, normal
/// completion or root death freezes and kills every verified coalition member
/// and boots out only its own label before exiting;</item>
/// <item><c>--mac-job &lt;control.json&gt;</c>: runs inside the launchd job,
/// atomically registers its pid/pidversion/coalition as protected metadata,
/// waits for the go file under a bounded deadline, runs the exact supplied
/// command with inherited stdio, and records the direct exit code.</item>
/// </list>
/// Both fail closed: any state that cannot be verified through libproc is
/// reported as an error, never as stopped. Nothing here ever starts the model
/// command outside the launchd job, and the task command is only signalled via
/// verified audit tokens from <see cref="MacProcessProbe.TryStopCoalition"/>.
/// </summary>
internal static class MacJobSupervisor
{
    internal const int FailClosedExitCode = 125;
    private const int LeaseLineLimit = 16;

    public static int RunWatchdog(string controlPath)
    {
        try
        {
            if (!OperatingSystem.IsMacOS())
            {
                return Error("the macOS watchdog requires macOS.");
            }
            return Watchdog(Path.GetFullPath(controlPath));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Error("watchdog failed: " + error.Message);
        }
    }

    public static int RunJob(string controlPath)
    {
        try
        {
            if (!OperatingSystem.IsMacOS())
            {
                return Error("the macOS job runner requires macOS.");
            }
            return Job(Path.GetFullPath(controlPath));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Error("job failed: " + error.Message);
        }
    }

    private static int Watchdog(string controlPath)
    {
        MacJobControl.RequireOwnedRegularFile(controlPath);
        var config = MacJobControl.ReadConfig(controlPath);
        if (config.BootSession != MacProcessProbe.BootSession) return Error("the job belongs to a previous macOS boot.");
        var self = MacProcessProbe.GetCurrent();
        var hostStatus = TryGetHostCoalition(out var hostCoalition, out var hostError);
        if (hostStatus == MacProbeStatus.Failed)
        {
            return Error("the host coalition could not be verified; refusing to start: " + hostError);
        }
        ulong? host = hostStatus == MacProbeStatus.Found ? hostCoalition : null;

        using var lease = new LeaseReader();
        lease.Start();
        WaitHandle.WaitAny(new[] { lease.Started.WaitHandle, lease.Closed.WaitHandle });
        if (!lease.StartSeen)
        {
            return lease.Invalid
                ? Error("unexpected data arrived on the lifetime lease before START.")
                : 0; // The host exited before creating a job; nothing was started.
        }
        if (!File.Exists(MacJobControl.PromptPath(config.JobRoot)))
        {
            return Error("the prompt file is missing; refusing to start the job.");
        }

        var plistPath = MacJobControl.PlistPath(config.JobRoot);
        MacJobControl.WriteAtomic(plistPath, Encoding.UTF8.GetBytes(BuildPlist(config)));
        var domain = "gui/" + MacProcessProbe.EffectiveUserId;
        if (!RunLaunchctl(out var bootstrapCode, out var bootstrapOutput, "bootstrap", domain, plistPath) ||
            bootstrapCode != 0)
        {
            var detail = bootstrapOutput.Length > 0 ? bootstrapOutput : "exit code " + bootstrapCode;
            StopAndBootout(config.Label, null, out _);
            return Error("launchctl bootstrap failed: " + detail);
        }

        // Wait for the job to register its protected identity. Until then the
        // go file is withheld, so the task command cannot have started.
        var identity = default(MacProcessIdentity);
        var deadline = Environment.TickCount64 + MacJobControl.BootstrapDeadlineMs;
        while (true)
        {
            if (lease.Closed.IsSet)
            {
                var late = ReadCleanupIdentity(config.JobRoot, self, host);
                return Abort(config, late ?? default, "the host lifetime lease closed while the job was starting.",
                    killCoalition: late is not null);
            }
            if (!Directory.Exists(MacJobControl.ControlDirectory(config.JobRoot)))
            {
                return Abort(config, default, "the job control directory disappeared while the job was starting.",
                    killCoalition: false);
            }
            var status = MacJobControl.TryReadIdentity(config.JobRoot, out var candidate, out var identityError);
            if (status == MacMetadataStatus.Ok)
            {
                identity = candidate;
                break;
            }
            if (status == MacMetadataStatus.Invalid)
            {
                return Abort(config, default, "the job identity metadata is invalid: " + identityError,
                    killCoalition: false);
            }
            if (Environment.TickCount64 > deadline)
            {
                var late = ReadCleanupIdentity(config.JobRoot, self, host);
                return Abort(config, late ?? default,
                    $"the job did not register its identity within {MacJobControl.BootstrapDeadlineMs} ms.",
                    killCoalition: late is not null);
            }
            Thread.Sleep(40);
        }

        // The task coalition must be distinct from both the watchdog's own
        // coalition and the host's: anything else would make a coalition kill
        // reach the host or the watchdog itself.
        if (!ValidateIdentity(identity, self, host, out var rejection))
        {
            return Abort(config, default, "the job identity was rejected: " + rejection, killCoalition: false);
        }
        if (lease.Closed.IsSet)
        {
            return Abort(config, identity, "the host lifetime lease closed before the job was released to run.");
        }
        try
        {
            MacJobControl.WriteAtomic(MacJobControl.GoPath(config.JobRoot), MacJobControl.BuildIdentityJson(identity));
        }
        catch (Exception goError) when (goError is IOException or UnauthorizedAccessException)
        {
            return Abort(config, identity, "the go file could not be written: " + goError.Message);
        }

        var stopPumps = 0;
        string? pumpError = null;
        var stdoutPump = StartPump(MacJobControl.JobStdoutPath(config.JobRoot), Console.OpenStandardOutput(),
            () => Volatile.Read(ref stopPumps) == 1, message => Interlocked.CompareExchange(ref pumpError, message, null));
        var stderrPump = StartPump(MacJobControl.JobStderrPath(config.JobRoot), Console.OpenStandardError(),
            () => Volatile.Read(ref stopPumps) == 1, message => Interlocked.CompareExchange(ref pumpError, message, null));

        int? exitCode = null;
        string? completionError = null;
        var cancelled = false;
        while (true)
        {
            if (Volatile.Read(ref pumpError) is { } failure) { completionError = failure; break; }
            if (new FileInfo(MacJobControl.JobStdoutPath(config.JobRoot)).Length > MacJobControl.LogLimitBytes ||
                new FileInfo(MacJobControl.JobStderrPath(config.JobRoot)).Length > MacJobControl.LogLimitBytes)
            {
                completionError = "task output exceeded the 64 MiB per-stream disk budget.";
                break;
            }
            var exitStatus = MacJobControl.TryReadExitCode(config.JobRoot, out var code, out var exitError);
            if (exitStatus == MacMetadataStatus.Ok)
            {
                exitCode = code;
                break;
            }
            if (exitStatus == MacMetadataStatus.Invalid)
            {
                completionError = "the job exit metadata is invalid: " + exitError;
                break;
            }
            if (lease.Closed.IsSet)
            {
                cancelled = true;
                break;
            }
            if (!Directory.Exists(MacJobControl.ControlDirectory(config.JobRoot)))
            {
                completionError = "the job control directory disappeared while the job was running.";
                break;
            }
            var aliveStatus = MacProcessProbe.CheckAlive(identity, out var aliveError);
            if (aliveStatus == MacProbeStatus.Failed)
            {
                completionError = "the job process state could not be verified: " + aliveError;
                break;
            }
            if (aliveStatus == MacProbeStatus.Vanished)
            {
                if (!WaitForExitMetadata(config.JobRoot, MacJobControl.IdentityDrainMs))
                {
                    completionError = "the job process exited without recording an exit code.";
                    break;
                }
                continue;
            }
            Thread.Sleep(80);
        }

        // Always freeze and kill every verified coalition member (detached
        // children included), then remove only our own launchd label, before
        // deciding the exit status.
        var stopped = MacProcessProbe.TryStopCoalition(identity.CoalitionId, out var coalitionError);
        var labelGone = EnsureLabelGone(config.Label, out var labelError);
        Volatile.Write(ref stopPumps, 1);
        var outputDrained = stdoutPump.Join(10_000);
        var errorDrained = stderrPump.Join(10_000);
        if (!outputDrained || !errorDrained) return Error("task output could not be drained before the deadline.");
        if (pumpError is not null) return Error(pumpError);

        if (!stopped)
        {
            return Error("coalition cleanup failed: " + coalitionError);
        }
        if (!labelGone)
        {
            return Error("launchd label cleanup failed: " + labelError);
        }
        if (completionError is not null)
        {
            return Error(completionError);
        }
        if (cancelled)
        {
            var late = MacJobControl.TryReadExitCode(config.JobRoot, out var lateCode, out _);
            return late == MacMetadataStatus.Ok ? lateCode : FailClosedExitCode;
        }
        if (exitCode is null)
        {
            return Error("the job exit code was not recorded.");
        }
        return exitCode.Value;
    }

    private static int Job(string controlPath)
    {
        MacJobControl.RequireOwnedRegularFile(controlPath);
        var config = MacJobControl.ReadConfig(controlPath);
        if (config.BootSession != MacProcessProbe.BootSession) return Error("the job belongs to a previous macOS boot.");
        var identity = MacProcessProbe.GetCurrent();
        MacJobControl.WriteAtomic(MacJobControl.IdentityPath(config.JobRoot),
            MacJobControl.BuildIdentityJson(identity));

        var goPath = MacJobControl.GoPath(config.JobRoot);
        var deadline = Environment.TickCount64 + MacJobControl.GoDeadlineMs;
        while (!File.Exists(goPath))
        {
            if (!Directory.Exists(MacJobControl.ControlDirectory(config.JobRoot)))
            {
                return Error("the job control directory disappeared before the job was released to run.");
            }
            if (Environment.TickCount64 > deadline)
            {
                return Error($"the job was not released to run within {MacJobControl.GoDeadlineMs} ms.");
            }
            Thread.Sleep(25);
        }

        if (MacJobControl.TryReadOwnedFile(goPath, MacJobControl.MetadataMaxBytes, out var go, out _) != MacReadStatus.Ok ||
            !go.AsSpan().SequenceEqual(MacJobControl.BuildIdentityJson(identity)))
            return Error("the release gate does not match this job identity.");
        var command = config.Command;
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Clear();
        foreach (var pair in command.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        // The trusted runner caps bytes before they reach launchd's disk logs.
        // Pipes apply backpressure; model-controlled output cannot fill the volume.
        using var process = Process.Start(startInfo)
            ?? throw new DockException("mac-job-start", "The task command could not be started.");
        var stdout = Task.Run(() => CopyBounded(process.StandardOutput.BaseStream, Console.OpenStandardOutput()));
        var stderr = Task.Run(() => CopyBounded(process.StandardError.BaseStream, Console.OpenStandardError()));
        var exceeded = false;
        while (!process.WaitForExit(40))
        {
            if (stdout.IsCompleted && !stdout.GetAwaiter().GetResult() || stderr.IsCompleted && !stderr.GetAwaiter().GetResult())
            {
                exceeded = true;
                break;
            }
        }
        // Keep only this trusted runner alive to drain its pipes. Detached tools
        // retaining stdout cannot hold completion open or write after completion.
        if (!MacProcessProbe.TryStopCoalition(identity.CoalitionId, out var cleanupError, identity))
            return Error("job child cleanup failed: " + cleanupError);
        var drained = Task.WaitAll([stdout, stderr], 10_000);
        exceeded |= !drained || !stdout.GetAwaiter().GetResult() || !stderr.GetAwaiter().GetResult();
        var exitCode = exceeded ? Error("task output exceeded the 64 MiB per-stream disk budget or could not be relayed.") : process.ExitCode;
        MacJobControl.WriteAtomic(MacJobControl.ExitCodePath(config.JobRoot), MacJobControl.BuildExitJson(exitCode));
        return exitCode;
    }

    private static bool CopyBounded(Stream source, Stream target)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (total + read > MacJobControl.LogLimitBytes) return false;
                target.Write(buffer, 0, read);
                target.Flush();
                total += read;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { return false; }
    }

    /// <summary>
    /// Freezes and kills the verified coalition (when known) and removes only
    /// this job's own launchd label, verifying each step. Exposed to the host
    /// side so a dead watchdog can be cleaned up through the same protected
    /// metadata before completion is permitted. Returns false with a bounded
    /// reason when the state cannot be verified as clean.
    /// </summary>
    internal static bool StopAndBootout(string label, MacProcessIdentity? identity, out string? error)
    {
        error = null;
        var failures = new List<string>();
        if (identity is { } known)
        {
            var self = MacProcessProbe.GetCurrent();
            if (known.CoalitionId == 0)
            {
                failures.Add("the recorded coalition id is 0; refusing to signal");
            }
            else if (known.CoalitionId == self.CoalitionId)
            {
                failures.Add("the recorded coalition equals the caller coalition; refusing to signal");
            }
            else if (!MacProcessProbe.TryStopCoalition(known.CoalitionId, out var coalitionError))
            {
                failures.Add(coalitionError ?? "coalition members remain");
            }
        }
        if (!EnsureLabelGone(label, out var labelError))
        {
            failures.Add(labelError ?? "launchd label cleanup failed");
        }
        if (failures.Count > 0)
        {
            error = MacJobControl.Truncate(string.Join(" ", failures), 1_500);
            return false;
        }
        return true;
    }

    private static int Abort(MacJobConfig config, MacProcessIdentity identity, string message,
        bool killCoalition = true)
    {
        var failures = new List<string>();
        if (killCoalition && identity.CoalitionId != 0)
        {
            var self = MacProcessProbe.GetCurrent();
            if (identity.CoalitionId == self.CoalitionId)
            {
                failures.Add("the recorded coalition equals the watchdog coalition; refusing to signal");
            }
            else if (!MacProcessProbe.TryStopCoalition(identity.CoalitionId, out var coalitionError))
            {
                failures.Add(coalitionError ?? "coalition members remain");
            }
        }
        if (!EnsureLabelGone(config.Label, out var labelError))
        {
            failures.Add(labelError ?? "launchd label cleanup failed");
        }
        return Error(failures.Count == 0 ? message : message + " " + string.Join(" ", failures));
    }

    private static bool ValidateIdentity(MacProcessIdentity identity, MacProcessIdentity self, ulong? host,
        out string? reason)
    {
        if (identity.Pid <= 0)
        {
            reason = "the recorded pid is invalid";
            return false;
        }
        if (identity.CoalitionId == 0)
        {
            reason = "the job did not receive an isolated resource coalition";
            return false;
        }
        if (identity.CoalitionId == self.CoalitionId)
        {
            reason = "the job coalition equals the watchdog coalition";
            return false;
        }
        if (host is { } hostCoalition && identity.CoalitionId == hostCoalition)
        {
            reason = "the job coalition equals the host coalition";
            return false;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = MacProcessProbe.TryGetIdentity(identity.Pid, out var live, out var error);
            if (status == MacProbeStatus.Found)
            {
                if (live.Version == identity.Version && live.CoalitionId == identity.CoalitionId)
                {
                    reason = null;
                    return true;
                }
                reason = "the recorded identity does not match the live process";
                return false;
            }
            if (status == MacProbeStatus.Vanished)
            {
                reason = "the job process vanished during bootstrap";
                return false;
            }
            if (attempt == 2)
            {
                reason = "the job identity could not be verified: " + error;
                return false;
            }
            Thread.Sleep(20);
        }
        reason = "the job identity could not be verified";
        return false;
    }

    /// <summary>
    /// Best-effort identity for cleanup during startup failures: only a
    /// metadata record whose coalition differs from the watchdog's and the
    /// host's is accepted for signalling; anything else means label-only
    /// cleanup.
    /// </summary>
    private static MacProcessIdentity? ReadCleanupIdentity(string jobRoot, MacProcessIdentity self, ulong? host)
    {
        var status = MacJobControl.TryReadIdentity(jobRoot, out var identity, out _);
        if (status != MacMetadataStatus.Ok)
        {
            return null;
        }
        if (identity.CoalitionId == 0 || identity.CoalitionId == self.CoalitionId)
        {
            return null;
        }
        if (host is { } hostCoalition && identity.CoalitionId == hostCoalition)
        {
            return null;
        }
        return identity;
    }

    /// <summary>
    /// Coalition of the process that started the watchdog (the host). A parent
    /// already reaped by init means the host is gone; failures are surfaced so
    /// the watchdog can fail closed instead of guessing.
    /// </summary>
    private static MacProbeStatus TryGetHostCoalition(out ulong coalition, out string? error)
    {
        coalition = 0;
        error = null;
        var parent = MacProcessProbe.ParentPid;
        if (parent <= 1)
        {
            return MacProbeStatus.Vanished;
        }
        var status = MacProcessProbe.TryGetIdentity(parent, out var identity, out error);
        if (status == MacProbeStatus.Found)
        {
            coalition = identity.CoalitionId;
        }
        return status;
    }

    private static bool EnsureLabelGone(string label, out string? error)
    {
        try
        {
            return EnsureLabelGoneCore(label, out error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = "launchd label cleanup failed: " + exception.Message;
            return false;
        }
    }

    private static bool EnsureLabelGoneCore(string label, out string? error)
    {
        error = null;
        var target = "gui/" + MacProcessProbe.EffectiveUserId + "/" + label;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = TryPrintLabel(target, out var queryError);
            if (state is null)
            {
                error = "the launchd label state could not be queried: " + queryError;
                return false;
            }
            if (state == false)
            {
                // Confirm once more shortly after: a killed watchdog may leave
                // an orphaned launchctl bootstrap completing behind it.
                Thread.Sleep(200);
                var confirm = TryPrintLabel(target, out var confirmError);
                if (confirm is null)
                {
                    error = "the launchd label state could not be confirmed: " + confirmError;
                    return false;
                }
                if (confirm == false)
                {
                    return true;
                }
            }
            RunLaunchctl(out _, out _, "bootout", target);
            Thread.Sleep(200);
        }
        var finalState = TryPrintLabel(target, out var finalError);
        if (finalState is null)
        {
            error = "the launchd label state could not be verified: " + finalError;
            return false;
        }
        if (finalState == false)
        {
            return true;
        }
        error = $"launchd job '{label}' is still loaded after bootout attempts.";
        return false;
    }

    /// <summary>true = loaded, false = not loaded, null = the query itself failed.</summary>
    private static bool? TryPrintLabel(string target, out string? error)
    {
        error = null;
        if (!RunLaunchctl(out var exitCode, out var output, "print", target))
        {
            error = output;
            return null;
        }
        if (exitCode == 0) return true;
        if (exitCode == 113) return false;
        error = "launchctl print failed with exit code " + exitCode + ": " + output;
        return null;
    }

    private static bool RunLaunchctl(out int exitCode, out string output, params string[] arguments)
    {
        exitCode = -1;
        output = "";
        try
        {
            var startInfo = new ProcessStartInfo("/bin/launchctl")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                output = "launchctl could not be started";
                return false;
            }
            var identified = MacProcessProbe.TryGetIdentity(process.Id, out var launchctlIdentity, out _) == MacProbeStatus.Found;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            // Close the inherited stdin so the helper cannot hold the lifetime
            // lease read end open.
            process.StandardInput.Close();
            if (!process.WaitForExit(MacJobControl.LaunchctlTimeoutMs))
            {
                try
                {
                    if (identified) MacProcessProbe.Signal(launchctlIdentity, MacProcessProbe.SignalKill);
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // The helper exited between the timeout and the kill.
                }
                process.WaitForExit(2_000);
                output = "launchctl timed out";
                return false;
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            output = MacJobControl.Truncate((stdout + " " + stderr).Trim(), 2_000);
            exitCode = process.ExitCode;
            return true;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            output = "launchctl could not be run: " + error.Message;
            return false;
        }
    }

    private static string BuildPlist(MacJobConfig config)
    {
        var dictionary = new XElement("dict");
        Add("Label", new XElement("string", config.Label));
        Add("ProgramArguments", new XElement("array", config.ProgramArguments.Select(static argument =>
            new XElement("string", argument))));
        Add("RunAtLoad", new XElement("true"));
        Add("StandardInPath", new XElement("string", MacJobControl.PromptPath(config.JobRoot)));
        Add("StandardOutPath", new XElement("string", MacJobControl.JobStdoutPath(config.JobRoot)));
        Add("StandardErrorPath", new XElement("string", MacJobControl.JobStderrPath(config.JobRoot)));
        Add("AbandonProcessGroup", new XElement("false"));
        Add("ProcessType", new XElement("string", "Interactive"));
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + Environment.NewLine + document.ToString();

        void Add(string key, XElement value)
        {
            dictionary.Add(new XElement("key", key));
            dictionary.Add(value);
        }
    }

    /// <summary>
    /// Follows one growing job log file and relays new bytes to the watchdog's
    /// own stdio, without buffering more than one chunk. Stops once
    /// <paramref name="finished"/> reports that cleanup verified no writers
    /// remain and the file has been drained.
    /// </summary>
    private static Thread StartPump(string path, Stream target, Func<bool> finished, Action<string> failed)
    {
        var thread = new Thread(() =>
        {
            try
            {
                FileStream? stream = null;
                var openDeadline = Environment.TickCount64 + 5_000;
                while (stream is null)
                {
                    try
                    {
                        stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (DirectoryNotFoundException)
                    {
                    }
                    if (stream is null && Environment.TickCount64 > openDeadline)
                    {
                        failed("task output file did not become available.");
                        return;
                    }
                    if (stream is null)
                    {
                        Thread.Sleep(25);
                    }
                }
                using (stream)
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = stream.Read(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            target.Write(buffer, 0, read);
                            target.Flush();
                            continue;
                        }
                        if (finished())
                        {
                            return;
                        }
                        Thread.Sleep(40);
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException
                or UnauthorizedAccessException)
            {
                failed("task output could not be relayed: " + error.GetType().Name);
            }
        })
        {
            IsBackground = true,
            Name = "agentdock-mac-pump",
        };
        thread.Start();
        return thread;
    }

    private static bool WaitForExitMetadata(string jobRoot, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 <= deadline)
        {
            if (MacJobControl.TryReadExitCode(jobRoot, out _, out _) != MacMetadataStatus.Missing)
            {
                return true;
            }
            Thread.Sleep(25);
        }
        return MacJobControl.TryReadExitCode(jobRoot, out _, out _) != MacMetadataStatus.Missing;
    }

    private static int Error(string message)
    {
        try
        {
            Console.Error.WriteLine(MacJobControl.WatchdogErrorPrefix + MacJobControl.Truncate(message, MacJobControl.ErrorMaxChars));
        }
        catch (IOException)
        {
        }
        return FailClosedExitCode;
    }

    /// <summary>
    /// Reads the lifetime lease from stdin: exactly one "START" line, then a
    /// drain until EOF. EOF without START means the host died before any job
    /// existed; any other byte before START is a protocol violation the
    /// watchdog refuses to proceed on.
    /// </summary>
    private sealed class LeaseReader : IDisposable
    {
        private readonly object _disposeGate = new();
        private bool _disposed;
        private readonly ManualResetEventSlim _started = new(false);
        private readonly ManualResetEventSlim _closed = new(false);
        private int _startSeen;
        private int _invalid;

        public bool StartSeen => Volatile.Read(ref _startSeen) == 1;

        public bool Invalid => Volatile.Read(ref _invalid) == 1;

        public ManualResetEventSlim Started => _started;

        public ManualResetEventSlim Closed => _closed;

        public void Start()
        {
            var thread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "agentdock-mac-lease",
            };
            thread.Start();
        }

        private void ReadLoop()
        {
            try
            {
                using var stdin = Console.OpenStandardInput();
                var line = new List<byte>(LeaseLineLimit);
                var buffer = new byte[1024];
                while (true)
                {
                    var read = stdin.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    for (var index = 0; index < read; index++)
                    {
                        var value = buffer[index];
                        if (value == (byte)'\n')
                        {
                            if (!string.Equals(Encoding.ASCII.GetString(line.ToArray()), "START",
                                    StringComparison.Ordinal))
                            {
                                Volatile.Write(ref _invalid, 1);
                                return;
                            }
                            Volatile.Write(ref _startSeen, 1);
                            _started.Set();
                            Drain(stdin);
                            return;
                        }
                        line.Add(value);
                        if (line.Count > LeaseLineLimit)
                        {
                            Volatile.Write(ref _invalid, 1);
                            return;
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The host side vanished; EOF semantics still apply.
            }
            finally
            {
                lock (_disposeGate) { if (!_disposed) _closed.Set(); }
            }
        }

        private static void Drain(Stream stdin)
        {
            var buffer = new byte[4096];
            while (stdin.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        public void Dispose()
        {
            lock (_disposeGate)
            {
                _disposed = true;
                _started.Dispose();
                _closed.Dispose();
            }
        }
    }
}
