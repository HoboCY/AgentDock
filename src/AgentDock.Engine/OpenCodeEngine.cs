using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// Production OpenCode execution engine: pinned OpenCode 1.18.30 behind the
/// pinned Codex sandbox, with a per-job loopback capability relay,
/// OS process containment, isolated configuration and before/after
/// project change observation.
/// </summary>
public sealed class OpenCodeEngine(string home, Func<string?> getCredential) : IExecutionEngine
{
    /// <summary>Exact OpenCode CLI version accepted at run time.</summary>
    public const string RequiredOpenCodeVersion = Pins.OpenCodeVersion;

    /// <summary>Exact sandbox version output accepted at run time.</summary>
    public const string RequiredSandboxOutput = Pins.CodexVersionOutput;

    /// <summary>Model this engine is pinned to; no other model is forwarded.</summary>
    public const string Model = Pins.Model;

    public static void CleanupArtifacts(string home, IEnumerable<string> keepTaskIds) => CheckArtifactsStore.Cleanup(home, keepTaskIds);

    /// <summary>Call only while owning the Host launch lock, before admitting any tasks.</summary>
    public static void RecoverAbandonedJobs(string home)
    {
        if (OperatingSystem.IsMacOS()) MacJobProcess.RecoverAbandonedJobs(home);
        var root = Path.Combine(Path.GetFullPath(home), "engine");
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var directory in new DirectoryInfo(root).GetDirectories())
        {
            // ExecutionSetup appends a hyphen and eight hexadecimal characters.
            var name = directory.Name;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || name.Length <= 9 || name[^9] != '-' ||
                !name.AsSpan(name.Length - 8).ToString().All(Uri.IsHexDigit)) continue;
            var taskId = name[..^9];
            CheckArtifactsStore.Retain(home, taskId, Path.Combine(directory.FullName, "runtime", "check-artifacts"));
            TryDelete(directory.FullName);
        }
    }

    // These native pipe reads/writes and exit waits block for the job's lifetime.
    // Dedicated threads keep them from starving Host requests and relay continuations.
    private static Task RunBlockingIo(Action operation) => Task.Factory.StartNew(operation, CancellationToken.None,
        TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    private static Task<T> RunBlockingIo<T>(Func<T> operation) => Task.Factory.StartNew(operation, CancellationToken.None,
        TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    private const int MaxPromptBytes = 1024 * 1024;
    private const int MaxErrorLength = 8_000;
    private const string ChangesDisclaimer =
        "[AgentDock] Observed {0} changed project file(s) by comparing before/after SHA-256 hashes. " +
        "This is an observation, not attribution: other processes may also have modified the project while the task ran.";

    private EngineHooks _hooks = EngineHooks.Default;

    internal OpenCodeEngine(string home, Func<string?> getCredential, EngineHooks hooks) : this(home, getCredential)
    {
        _hooks = hooks;
    }

    public async Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new DockException("engine-home-missing", "The AgentDock engine home directory is not configured.");
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            throw new DockException("platform-unsupported",
                "OpenCode execution requires the Windows or macOS sandbox.");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return new ExecutionResult(false, "", "Cancelled before the task started.");
        }

        var settings = task.Settings;
        var enginePath = RequireFile(settings?.EnginePath, "engine-missing",
            "The pinned OpenCode executable is not configured or does not exist.");
        var sandboxPath = RequireFile(settings?.SandboxPath, "sandbox-missing",
            "The pinned Codex sandbox executable is not configured or does not exist.");
        RequireFile(settings?.ShellPath, "shell-missing",
            "The configured shell executable is not configured or does not exist.");
        if (!string.IsNullOrEmpty(task.Prompt) && Encoding.UTF8.GetByteCount(task.Prompt) > MaxPromptBytes)
        {
            throw new DockException("prompt-invalid", "The task prompt is larger than the supported bound.");
        }

        var credential = getCredential()
            ?? throw new DockException("credential-missing", "The OpenCode Go credential is not configured.");
        CredentialVault.Validate(credential);

        try
        {
            await CheckVersionsAsync(enginePath, sandboxPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ExecutionResult(false, "", "Cancelled before the task started.");
        }

        var timeoutSeconds = task.Configuration?.TimeoutSeconds ?? 1200;
        var adapter = new OpenCodeEventAdapter();
        GoRelay? relay = null;
        IJobProcess? process = null;
        Dictionary<string, string>? before = null;
        string? jobRoot = null;
        string? checkArtifacts = null;
        string? retainedArtifacts = null;
        string? runtimeError = null;
        FileChange[] changes = [];

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ExecutionResult(false, "", "Cancelled before the task started.");
            }
            relay = await GoRelay.StartAsync(credential, task.Id, _hooks.RelayTransport, cancellationToken)
                .ConfigureAwait(false);
            var prepared = ExecutionSetup.Prepare(task, Path.GetFullPath(home), relay, _hooks.RunnerExecutable);
            jobRoot = prepared.JobRoot;
            checkArtifacts = prepared.CheckArtifacts;
            before = ProjectSnapshot.Capture(task.Project);

            process = _hooks.ProcessStarter is { } starter
                ? starter(prepared.SandboxExecutable, prepared.SandboxArguments, task.Project, prepared.SandboxEnvironment)
                : OperatingSystem.IsMacOS()
                    ? MacJobProcess.Start(prepared.SandboxExecutable, prepared.SandboxArguments, task.Project, prepared.SandboxEnvironment)
                    : JobProcess.Start(prepared.SandboxExecutable, prepared.SandboxArguments, task.Project, prepared.SandboxEnvironment);
            var job = process;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeoutSeconds > 0)
            {
                linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            }
            using var registration = linked.Token.Register(static state =>
            {
                try
                {
                    ((IJobProcess)state!).Terminate();
                }
                catch (Exception error) when (error is Win32Exception or ObjectDisposedException or InvalidOperationException)
                {
                    // The tree already exited.
                }
                catch (DockException)
                {
                    // Cancellation callbacks cannot throw on the timer thread.
                    // WaitForExit/Dispose repeat verification and surface an unclean stop.
                }
            }, job);

            var prompt = task.Prompt ?? "";
            _ = RunBlockingIo(() =>
            {
                try
                {
                    job.Stdin.Write(Encoding.UTF8.GetBytes(prompt));
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The child never read its prompt or already died.
                }
                finally
                {
                    try
                    {
                        job.Stdin.Dispose();
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException)
                    {
                        // Already closed.
                    }
                }
            });

            long consumedMeasurement = 0;
            var reportFailure = (Exception?)null;
            var stdoutTask = RunBlockingIo(() =>
            {
                try
                {
                    foreach (var line in JobProcess.ReadLines(job.Stdout))
                    {
                        foreach (var original in adapter.Feed(line))
                        {
                            var engineEvent = original;
                            if (engineEvent.Kind == "usage")
                            {
                                var (sequence, seconds) = relay.LatestMeasurement;
                                if (sequence > consumedMeasurement)
                                {
                                    // Skipped requests (for example compaction) make phase attribution ambiguous.
                                    engineEvent = engineEvent with { GenerationSeconds = sequence == consumedMeasurement + 1 ? seconds : null };
                                    consumedMeasurement = sequence;
                                }
                            }
                            if (engineEvent.Text.Length > 0)
                            {
                                engineEvent = engineEvent with
                                {
                                    Text = Scrub(engineEvent.Text, credential, relay.Token),
                                };
                            }
                            report(engineEvent);
                        }
                    }
                }
                catch (Exception error)
                {
                    reportFailure = error;
                }
            });
            var stderrTask = RunBlockingIo(() => ReadTail(job.Stderr, 8_000));

            var exitTask = RunBlockingIo(() => job.WaitForExit(-1));
            await exitTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            if (reportFailure is not null)
            {
                throw new DockException("engine-callback-failed", "The event report callback failed.");
            }
            var exitCode = job.ExitCode;
            var stderrTail = await stderrTask.ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                runtimeError = "Cancelled by the caller; file changes were retained.";
            }
            else if (linked.IsCancellationRequested)
            {
                runtimeError = $"Timed out after {timeoutSeconds} seconds; file changes were retained.";
            }
            else if (exitCode != 0 || !adapter.CanComplete(exitCode))
            {
                var detail = adapter.Failure;
                if (string.IsNullOrEmpty(detail) && !string.IsNullOrEmpty(stderrTail))
                {
                    detail = stderrTail;
                }
                if (string.IsNullOrEmpty(detail))
                {
                    detail = exitCode != 0
                        ? $"OpenCode exited with code {exitCode}."
                        : "OpenCode finished without a terminal stop; the turn is treated as incomplete.";
                }
                runtimeError = Scrub(detail, credential, relay.Token);
            }
        }
        catch (OperationCanceledException)
        {
            runtimeError = "Cancelled before any process started.";
        }
        catch (DockException)
        {
            throw;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or JsonException)
        {
            runtimeError = Scrub(error.Message, credential, relay?.Token ?? "");
        }
        finally
        {
            process?.Dispose();
            if (relay is not null)
            {
                await relay.DisposeAsync().ConfigureAwait(false);
            }
            if (before is not null)
            {
                changes = ProjectSnapshot.Diff(before, ProjectSnapshot.Capture(task.Project));
            }
            if (checkArtifacts is not null)
            {
                retainedArtifacts = CheckArtifactsStore.Retain(home, task.Id, checkArtifacts);
            }
            if (jobRoot is not null)
            {
                TryDelete(jobRoot);
            }
        }

        if (runtimeError is not null && runtimeError.Length > MaxErrorLength)
        {
            runtimeError = runtimeError[..MaxErrorLength];
        }
        var resultText = Scrub(adapter.Result, credential, relay?.Token ?? "");
        if (retainedArtifacts is not null) resultText += "\n\n[AgentDock] Check artifacts: " + retainedArtifacts + " (retained with task body; capacity limits apply).";
        if (changes.Length > 0)
        {
            var note = string.Format(ChangesDisclaimer, changes.Length);
            resultText = resultText.Length > 0 ? resultText + "\n\n" + note : note;
        }
        return new ExecutionResult(runtimeError is null, resultText, runtimeError, changes);
    }

    private async Task CheckVersionsAsync(string enginePath, string sandboxPath, CancellationToken cancellationToken)
    {
        var engineVersion = await ProbeVersionAsync(enginePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(engineVersion, Pins.OpenCodeVersion, StringComparison.Ordinal))
        {
            throw new DockException("engine-version",
                $"OpenCode must be exactly {Pins.OpenCodeVersion}; no automatic upgrade or fallback.");
        }
        var sandboxVersion = await ProbeVersionAsync(sandboxPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sandboxVersion, Pins.CodexVersionOutput, StringComparison.Ordinal))
        {
            throw new DockException("sandbox-version",
                $"The Codex sandbox must be exactly {Pins.CodexVersionOutput}; no automatic upgrade or fallback.");
        }
    }

    private async Task<string> ProbeVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var probe = _hooks.VersionProbe ?? DefaultVersionProbe;
        var output = await probe(executable, cancellationToken).ConfigureAwait(false);
        return output.Trim();
    }

    private static async Task<string> DefaultVersionProbe(string executable, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--version");
        if (!process.Start())
        {
            throw new Win32Exception("Failed to start the version probe.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, 4_096, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 4_096, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return await outputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new DockException("version-probe-timeout",
                "The pinned executable did not report its version in time.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (builder.Length < maxCharacters)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxCharacters - builder.Length)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static string ReadTail(Stream stream, int maxCharacters)
    {
        try
        {
            var reader = new StreamReader(stream, Encoding.UTF8);
            var tail = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > maxCharacters * 2)
                {
                    tail.Remove(0, tail.Length - maxCharacters);
                }
            }
            return tail.Length <= maxCharacters ? tail.ToString() : tail.ToString(tail.Length - maxCharacters, maxCharacters);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return "";
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static string RequireFile(string? path, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new DockException(code, message);
        }
        return Path.GetFullPath(path);
    }

    internal static string Scrub(string text, string credential, string capability)
    {
        var result = text;
        if (credential.Length > 0)
        {
            result = result.Replace(credential, "[redacted]", StringComparison.Ordinal);
        }
        if (capability.Length > 0)
        {
            result = result.Replace(capability, "[redacted]", StringComparison.Ordinal);
        }
        return result;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Scratch data may stay behind when a sandboxed handle is still open.
        }
    }
}

/// <summary>Test-only seams; production uses the defaults.</summary>
internal sealed record EngineHooks
{
    public static EngineHooks Default { get; } = new();

    public Func<string, CancellationToken, Task<string>>? VersionProbe { get; init; }

    public HttpMessageHandler? RelayTransport { get; init; }

    public string? RunnerExecutable { get; init; }

    /// <summary>
    /// Replaces the outer process start. Tests use it to exercise the real
    /// in-sandbox runner before OS-level sandbox validation is available; the
    /// production path always starts the pinned Codex sandbox.
    /// </summary>
    public Func<string, IReadOnlyList<string>, string, IReadOnlyDictionary<string, string>, IJobProcess>? ProcessStarter { get; init; }
}
