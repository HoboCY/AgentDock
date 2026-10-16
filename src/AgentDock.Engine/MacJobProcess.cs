using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// macOS counterpart of <see cref="JobProcess"/>: runs one task command inside
/// a dedicated launchd job whose processes share a private resource coalition,
/// and stops it only through audit-token signals verified by libproc
/// (see <see cref="MacProcessProbe.TryStopCoalition"/>). There is no
/// unsandboxed fallback and no bare-pid signalling.
///
/// The host process here starts a separate Engine
/// <c>--mac-watchdog &lt;control.json&gt;</c> helper with redirected stdio. The
/// watchdog's stdin is the host lifetime lease: when this process exits or
/// disposes, the pipe reaches EOF and the watchdog freezes and kills every
/// verified coalition member before removing its own launchd label. The pipe
/// handles are close-on-exec (the runtime default for redirected stdio), and
/// the watchdog itself never hands its stdin to a spawned helper, so no other
/// process can keep the lease open past the host. The
/// returned <see cref="Stdin"/> is a bounded in-memory stream: on dispose it
/// atomically writes the prompt file and sends START to the watchdog, but it
/// does not close the lease. <see cref="Stdout"/> and <see cref="Stderr"/> are
/// the watchdog's relayed streams.
///
/// Job control files live under <c>&lt;jobRoot&gt;/control</c>, outside any
/// runtime directory the task may write to; denying the task access to them is
/// the caller's responsibility.
/// </summary>
internal sealed class MacJobProcess : IJobProcess
{
    private const int TerminateWaitMilliseconds = 15_000;
    private const int DisposeWaitMilliseconds = 15_000;
    private static readonly byte[] StartMarker = Encoding.ASCII.GetBytes("START\n");

    private readonly Process _watchdog;
    internal MacProcessIdentity WatchdogIdentity { get; }
    private readonly Stream _lease;
    private readonly MacJobStdin _stdin;
    private readonly string _jobRoot;
    private readonly string _label;
    private readonly object _gate = new();
    private readonly object _stopGate = new();
    private volatile bool _startRequested;
    private volatile bool _terminated;
    private volatile bool _disposed;
    private bool _stopVerified;

    private MacJobProcess(string jobRoot, Process watchdog)
    {
        _jobRoot = jobRoot;
        _label = MacJobControl.LabelFor(jobRoot);
        _watchdog = watchdog;
        if (MacProcessProbe.TryGetIdentity(watchdog.Id, out var identity, out _) == MacProbeStatus.Found)
            WatchdogIdentity = identity;
        _lease = watchdog.StandardInput.BaseStream;
        Stdout = watchdog.StandardOutput.BaseStream;
        Stderr = watchdog.StandardError.BaseStream;
        _stdin = new MacJobStdin(this);
    }

    /// <summary>Buffered task prompt; committed on dispose (prompt file + START).</summary>
    public Stream Stdin => _stdin;

    /// <summary>Raw task stdout relayed by the watchdog.</summary>
    public Stream Stdout { get; }

    /// <summary>Raw task stderr relayed by the watchdog, plus watchdog diagnostics.</summary>
    public Stream Stderr { get; }

    public bool HasExited
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _watchdog.HasExited;
        }
    }

    /// <summary>
    /// Starts one macOS job. <paramref name="executable"/>, its
    /// <paramref name="arguments"/>, <paramref name="cwd"/> and
    /// <paramref name="environment"/> describe the exact command to run inside
    /// the launchd job. The job root defaults to a fresh directory under
    /// <c>&lt;temp&gt;/agentdock-mac-jobs</c>; the caller may provide the exact
    /// root through the <see cref="MacJobControl.ReservedRootVariable"/>
    /// environment entry, which is stripped from the job environment.
    /// </summary>
    public static MacJobProcess Start(string executable, IReadOnlyList<string> arguments, string cwd,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The macOS job backend requires macOS.");
        }
        var fullExecutable = Path.GetFullPath(executable);
        if (!File.Exists(fullExecutable))
        {
            throw new FileNotFoundException("The task executable was not found.", fullExecutable);
        }
        var fullCwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(fullCwd))
        {
            throw new DirectoryNotFoundException("The task working directory no longer exists.");
        }

        var jobEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        string? requestedRoot = null;
        foreach (var pair in environment)
        {
            if (string.Equals(pair.Key, MacJobControl.ReservedRootVariable, StringComparison.Ordinal))
            {
                requestedRoot = pair.Value;
                continue;
            }
            jobEnvironment[pair.Key] = pair.Value;
        }

        var (jobRoot, createdRoot) = PrepareJobRoot(requestedRoot);
        try
        {
            var controlPath = MacJobControl.ConfigPath(jobRoot);
            var runner = ExecutionSetup.ResolveRunner(null);
            var programArguments = new string[runner.Length + 2];
            Array.Copy(runner, programArguments, runner.Length);
            programArguments[^2] = "--mac-job";
            programArguments[^1] = controlPath;
            MacJobControl.WriteAtomic(controlPath,
                BuildConfig(jobRoot, programArguments, fullExecutable, arguments, fullCwd, jobEnvironment));
            return LaunchWatchdog(jobRoot, controlPath, runner);
        }
        catch
        {
            if (createdRoot)
            {
                TryDeleteDirectory(jobRoot);
            }
            throw;
        }
    }

    /// <summary>
    /// Waits for the watchdog within <paramref name="milliseconds"/> (negative =
    /// infinite). Once the watchdog has exited, the job state is verified
    /// through the protected metadata; a state that cannot be verified as
    /// stopped throws instead of reporting completion.
    /// </summary>
    public bool WaitForExit(int milliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var exited = _watchdog.WaitForExit(milliseconds);
        if (exited)
        {
            EnsureStopped();
        }
        return exited;
    }

    /// <summary>
    /// The task command's exit code from its protected exit metadata, or the
    /// watchdog's exit code when the job never recorded one. Only readable once
    /// the watchdog has exited and cleanup is verified.
    /// </summary>
    public int ExitCode
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_watchdog.HasExited)
            {
                throw new InvalidOperationException("The macOS job watchdog has not exited yet.");
            }
            EnsureStopped();
            if (_watchdog.ExitCode != 0) return _watchdog.ExitCode;
            var status = MacJobControl.TryReadExitCode(_jobRoot, out var exitCode, out _);
            return status == MacMetadataStatus.Ok ? exitCode : _watchdog.ExitCode;
        }
    }

    /// <summary>
    /// Closes the lifetime lease, waits for the watchdog to clean up by itself,
    /// and only then permits completion. A wedged watchdog is killed by its
    /// direct process handle and the same verified coalition cleanup runs from
    /// the host side. Throws when the stopped state cannot be verified.
    /// </summary>
    public void Terminate()
    {
        lock (_gate)
        {
            if (_disposed || _terminated)
            {
                return;
            }
            _terminated = true;
            _stdin.Abandon();
            CloseLease();
        }
        if (_watchdog.WaitForExit(TerminateWaitMilliseconds))
        {
            EnsureStopped();
            return;
        }
        TryKillWatchdog();
        EnsureStopped();
        if (!_watchdog.HasExited)
        {
            throw new DockException("mac-watchdog-stuck",
                "The macOS watchdog did not exit after termination; the job state could not be closed.");
        }
    }

    /// <summary>
    /// Cleans up for callers that never sent START, or that already ran
    /// <see cref="Terminate"/> or <see cref="WaitForExit"/>. Verifies actual
    /// cleanup through protected metadata before completing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_gate)
        {
            _stdin.Abandon();
            CloseLease();
        }
        try
        {
            if (!_watchdog.WaitForExit(DisposeWaitMilliseconds))
            {
                TryKillWatchdog();
            }
            EnsureStopped();
        }
        finally
        {
            _watchdog.Dispose();
            Stdout.Dispose();
            Stderr.Dispose();
            _lease.Dispose();
        }
    }

    /// <summary>
    /// Cleans process state left behind by a crashed host: only job folders
    /// that carry this backend's own control file are touched, only when the
    /// folder is owned by the current user, and only when the recorded identity
    /// (or the absence of one) is validated. Folders under the default
    /// temporary base are removed once verified clean; folders under
    /// <c>&lt;home&gt;/engine</c> are cleaned but left for the caller's scratch
    /// deletion. A cleanup that cannot be verified throws; it never silently
    /// reports those jobs cleared.
    /// </summary>
    public static void RecoverAbandonedJobs(string home)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(home))
            ScanRecoveryRoots(MacJobControl.DefaultJobRootBase, deleteClean: true, failures);
        else
        {
            ScanRecoveryRoots(Path.Combine(Path.GetFullPath(home), "engine"), deleteClean: false, failures);
        }
        if (failures.Count > 0)
        {
            throw new DockException("mac-recovery-failed",
                "Abandoned macOS job cleanup could not be verified: "
                + MacJobControl.Truncate(string.Join(" | ", failures.Take(5)), 2_000));
        }
    }

    internal void SendStart(byte[] prompt)
    {
        lock (_gate)
        {
            if (_disposed || _terminated || _startRequested)
            {
                return;
            }
            if (prompt.Length > MacJobControl.PromptMaxBytes)
            {
                throw new DockException("mac-prompt-invalid", "The task prompt is larger than the supported bound.");
            }
            MacJobControl.WriteAtomic(MacJobControl.PromptPath(_jobRoot), prompt);
            // Mark the request before it reaches the lease: if the write itself
            // fails, the caller still verifies the (nonexistent) job rather
            // than risking a completion that skipped cleanup.
            _startRequested = true;
            _lease.Write(StartMarker, 0, StartMarker.Length);
            _lease.Flush();
        }
    }

    /// <summary>
    /// Verifies the job is stopped before any completion is reported. When the
    /// watchdog already cleaned up this is a cheap idempotent rescan; when it
    /// died early, this performs the same coalition kill and label bootout
    /// through the protected identity metadata.
    /// </summary>
    private void EnsureStopped()
    {
        lock (_stopGate)
        {
            if (_stopVerified)
            {
                return;
            }
            if (!_startRequested)
            {
                // START was never requested, so the watchdog could not have
                // created a job; nothing exists to clean.
                _stopVerified = true;
                return;
            }
            var status = MacJobControl.TryReadIdentity(_jobRoot, out var identity, out var identityError);
            string? error;
            bool ok;
            if (status == MacMetadataStatus.Invalid)
            {
                MacJobSupervisor.StopAndBootout(_label, null, out _);
                ok = false;
                error = identityError ?? "the job identity metadata is unreadable";
            }
            else
            {
                MacProcessIdentity? known = status == MacMetadataStatus.Ok ? identity : null;
                ok = MacJobSupervisor.StopAndBootout(_label, known, out error);
                if (ok && status == MacMetadataStatus.Missing && File.Exists(MacJobControl.GoPath(_jobRoot)))
                {
                    ok = false;
                    error = "the job was released to run but its identity metadata is missing; "
                        + "the coalition cannot be verified.";
                }
            }
            if (!ok)
            {
                throw new DockException("mac-cleanup-failed",
                    MacJobControl.Truncate(error ?? "The macOS job could not be verified as stopped.", 2_000));
            }
            _stopVerified = true;
        }
    }

    private void CloseLease()
    {
        try
        {
            _lease.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Already closed by the far side.
        }
    }

    /// <summary>
    /// Kills only this directly-owned helper process by its process handle
    /// (never a bare pid and never a process tree). The task coalition is not
    /// touched here; it is always stopped through verified audit tokens.
    /// </summary>
    private void TryKillWatchdog()
    {
        try
        {
            if (WatchdogIdentity.Pid > 0) MacProcessProbe.Signal(WatchdogIdentity, MacProcessProbe.SignalKill);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The watchdog exited between the wait and the kill.
        }
        _watchdog.WaitForExit(3_000);
    }

    private static (string Root, bool Created) PrepareJobRoot(string? requestedRoot)
    {
        if (!string.IsNullOrWhiteSpace(requestedRoot))
        {
            if (!Path.IsPathFullyQualified(requestedRoot))
            {
                throw new DockException("mac-job-root",
                    $"{MacJobControl.ReservedRootVariable} must be an absolute path.");
            }
            var root = Path.GetFullPath(requestedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Length == 0 || !MacJobControl.IsValidLabelSuffix(Path.GetFileName(root)))
            {
                throw new DockException("mac-job-root",
                    "The requested job root name cannot be used as an AgentDock launchd label.");
            }
            var created = false;
            if (!Directory.Exists(root))
            {
                if (File.Exists(root))
                {
                    throw new DockException("mac-job-root", "The requested job root is an existing file.");
                }
                Directory.CreateDirectory(root);
                created = true;
            }
            if (created)
            {
                MacJobControl.SetPrivateMode(root, directory: true);
            }
            if (!MacFileIdentity.TryStat(root, out var kind, out var ownerUid, out var statError) ||
                kind != MacFileKind.Directory || ownerUid != MacProcessProbe.EffectiveUserId)
            {
                throw new DockException("mac-job-root",
                    "The requested job root is not a current-user directory: " + (statError ?? "ownership check failed"));
            }
            var control = MacJobControl.ControlDirectory(root);
            if (Directory.Exists(control) || File.Exists(control))
                throw new DockException("mac-job-root-used", "A macOS job control directory can only be used once.");
            Directory.CreateDirectory(control);
            MacJobControl.SetPrivateMode(control, directory: true);
            using (new FileStream(Path.Combine(control, "claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            return (root, created);
        }

        var baseDirectory = MacJobControl.DefaultJobRootBase;
        Directory.CreateDirectory(baseDirectory);
        MacJobControl.SetPrivateMode(baseDirectory, directory: true);
        var generated = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generated);
        MacJobControl.SetPrivateMode(generated, directory: true);
        var controlDirectory = MacJobControl.ControlDirectory(generated);
        Directory.CreateDirectory(controlDirectory);
        MacJobControl.SetPrivateMode(controlDirectory, directory: true);
        return (generated, true);
    }

    private static MacJobProcess LaunchWatchdog(string jobRoot, string controlPath, string[] runner)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runner[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = jobRoot,
        };
        for (var index = 1; index < runner.Length; index++)
        {
            startInfo.ArgumentList.Add(runner[index]);
        }
        startInfo.ArgumentList.Add("--mac-watchdog");
        startInfo.ArgumentList.Add(controlPath);
        var watchdog = new Process { StartInfo = startInfo };
        if (!watchdog.Start())
        {
            throw new DockException("mac-watchdog-start", "The macOS watchdog process could not be started.");
        }
        return new MacJobProcess(jobRoot, watchdog);
    }

    private static byte[] BuildConfig(string jobRoot, string[] programArguments, string executable,
        IReadOnlyList<string> arguments, string cwd, Dictionary<string, string> jobEnvironment)
    {
        var environment = new JsonObject();
        foreach (var pair in jobEnvironment)
        {
            environment[pair.Key] = pair.Value;
        }
        var root = new JsonObject
        {
            ["version"] = MacJobControl.ConfigVersion,
            ["label"] = MacJobControl.LabelFor(jobRoot),
            ["bootSession"] = MacProcessProbe.BootSession,
            ["jobRoot"] = jobRoot,
            ["programArguments"] = new JsonArray(programArguments.Select(static value => (JsonNode)value).ToArray()),
            ["command"] = new JsonObject
            {
                ["executable"] = executable,
                ["arguments"] = new JsonArray(arguments.Select(static value => (JsonNode)value).ToArray()),
                ["workingDirectory"] = cwd,
                ["environment"] = environment,
            },
        };
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        if (bytes.Length > MacJobControl.ConfigMaxBytes)
        {
            throw new DockException("mac-config-invalid",
                "The macOS job description exceeds the supported bound.");
        }
        return bytes;
    }

    private static void ScanRecoveryRoots(string baseDirectory, bool deleteClean, List<string> failures)
    {
        if (!Directory.Exists(baseDirectory))
        {
            return;
        }
        var scanned = 0;
        foreach (var candidate in Directory.EnumerateDirectories(baseDirectory))
        {
            scanned++;
            if (scanned > 512)
            {
                failures.Add($"more than 512 folders under '{baseDirectory}'; recovery stopped at its bound");
                break;
            }
            if (!MacFileIdentity.TryStat(candidate, out var kind, out var ownerUid, out var statError))
            {
                failures.Add(statError ?? $"could not inspect '{candidate}'");
                continue;
            }
            // Never touch anything that is not a current-user directory: other
            // launchd jobs and other users are out of scope by construction.
            if (kind != MacFileKind.Directory || ownerUid != MacProcessProbe.EffectiveUserId)
            {
                continue;
            }
            var controlPath = Path.Combine(candidate, "control", "job.json");
            if (!File.Exists(controlPath))
            {
                continue;
            }
            MacJobConfig config;
            try
            {
                config = MacJobControl.ReadConfig(controlPath);
            }
            catch (DockException configError)
            {
                failures.Add($"'{candidate}': {configError.Message}");
                continue;
            }
            // A reboot destroys every old job. Coalition IDs may then be reused;
            // old metadata must never be used to signal this boot's processes.
            if (config.BootSession != MacProcessProbe.BootSession) continue;
            var status = MacJobControl.TryReadIdentity(candidate, out var identity, out var identityError);
            MacProcessIdentity? known = status == MacMetadataStatus.Ok ? identity : null;
            var ok = MacJobSupervisor.StopAndBootout(config.Label, known, out var cleanupError);
            if (status == MacMetadataStatus.Invalid)
            {
                ok = false;
                cleanupError = identityError ?? "the job identity metadata is unreadable";
            }
            else if (ok && status == MacMetadataStatus.Missing && File.Exists(MacJobControl.GoPath(candidate)))
            {
                ok = false;
                cleanupError = "the job was released to run but its identity metadata is missing; "
                    + "the coalition cannot be verified.";
            }
            if (!ok)
            {
                failures.Add($"'{candidate}': {cleanupError}");
                continue;
            }
            if (deleteClean)
            {
                try
                {
                    Directory.Delete(candidate, recursive: true);
                }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"could not delete '{candidate}': {deleteError.Message}");
                }
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort; the caller's scratch deletion retries later.
        }
    }

    /// <summary>
    /// Bounded prompt buffer. Writes accumulate in memory; disposing the stream
    /// atomically writes the prompt file and sends START to the watchdog. It
    /// never closes the underlying lifetime lease, and it never sends START
    /// when the owner is tearing the job down.
    /// </summary>
    private sealed class MacJobStdin : Stream
    {
        private readonly MacJobProcess _owner;
        private readonly MemoryStream _buffer = new();
        private bool _disposed;

        public MacJobStdin(MacJobProcess owner) => _owner = owner;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MacJobStdin));
            }
            if (count <= 0)
            {
                return;
            }
            if (_buffer.Length + count > MacJobControl.PromptMaxBytes)
            {
                throw new InvalidOperationException(
                    $"The task prompt exceeds the supported {MacJobControl.PromptMaxBytes}-byte bound.");
            }
            _buffer.Write(buffer, offset, count);
        }

        /// <summary>Prompt delivery happens on dispose by design; nothing is sent early.</summary>
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }
            _disposed = true;
            try
            {
                if (disposing)
                {
                    _owner.SendStart(_buffer.ToArray());
                }
            }
            finally
            {
                _buffer.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>Drops the buffered prompt without sending START (job teardown).</summary>
        public void Abandon()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _buffer.Dispose();
        }
    }
}
