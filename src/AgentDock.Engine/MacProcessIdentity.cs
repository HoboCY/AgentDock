using System.Runtime.InteropServices;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// Verified identity of one macOS process: the pid, the pid version
/// ("pidversion") consumed by the audit token, and the resource coalition id.
/// A bare pid is never trusted for signalling; the full audit token makes a
/// recycled pid harmless.
/// </summary>
internal readonly record struct MacProcessIdentity(int Pid, uint Version, ulong CoalitionId);

/// <summary>Result of one libproc query. "Vanished" is an expected race; "Failed" is not.</summary>
internal enum MacProbeStatus { Found, Vanished, Failed }

/// <summary>
/// libproc/libSystem probes for the macOS job backend. Layout facts verified on
/// macOS 26.6.2 arm64:
/// <list type="bullet">
/// <item><c>proc_pidinfo</c> flavor 17 (<c>PROC_PIDUNIQIDENTIFIERINFO</c> in
/// &lt;sys/proc_info.h&gt;) returns 56 bytes; the 32-bit pid version used by the
/// audit token occupies offset 32;</item>
/// <item>flavor 20 (<c>PROC_PIDCOALITIONINFO</c>) returns 40 bytes with the
/// resource coalition id as a <c>uint64</c> at offset 0
/// (<c>COALITION_TYPE_RESOURCE</c> is index 0);</item>
/// <item><c>proc_signal_with_audittoken</c> takes a 32-byte audit token
/// (<c>uint32_t[8]</c>, pid at index 5, pid version at index 7) and returns
/// errno directly: 0 on success, 3 (ESRCH) when the recorded pid/version no
/// longer matches any process;</item>
/// <item><c>proc_listpids</c> selectors from libproc.h:
/// <c>PROC_ALL_PIDS=1</c>, <c>PROC_PGRP_ONLY=2</c>, <c>PROC_TTY_ONLY=3</c>,
/// <c>PROC_UID_ONLY=4</c>, <c>PROC_RUID_ONLY=5</c>, <c>PROC_PPID_ONLY=6</c>;
/// <c>PROC_UID_ONLY</c> uses the effective uid as the selector value.</item>
/// </list>
/// Every query failure is surfaced to the caller; callers must fail closed
/// because an unverifiable state is never reported as clean.
/// </summary>
internal static class MacProcessProbe
{
    private static readonly Lazy<string> Boot = new(ReadBootSession);
    public static string BootSession => Boot.Value;

    private static string ReadBootSession()
    {
        var bytes = new byte[128];
        nuint length = (nuint)bytes.Length;
        if (sysctlbyname("kern.bootsessionuuid", bytes, ref length, IntPtr.Zero, 0) != 0 || length > (nuint)bytes.Length)
            throw new DockException("mac-spi-unavailable", "Cannot identify this macOS boot session.");
        var value = System.Text.Encoding.ASCII.GetString(bytes, 0, (int)length).TrimEnd('\0');
        if (!Guid.TryParse(value, out var id)) throw new DockException("mac-spi-unavailable", "Invalid macOS boot identity.");
        return id.ToString("D");
    }

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    private static extern int sysctlbyname(string name, [Out] byte[] value, ref nuint size, IntPtr replacement, nuint replacementSize);
    private const int FlavorUniqueIdentifier = 17;
    private const int FlavorCoalition = 20;
    private const int UniqueIdentifierSize = 56;
    private const int PidVersionOffset = 32;
    private const int CoalitionSize = 40;
    private const int CoalitionIdOffset = 0;
    private const uint ListOwnUidSelector = 4;
    private const int ErrnoNoSuchProcess = 3;
    private const int PidListLimit = 16 * 1024;
    private const int FailureListLimit = 8;

    internal const int SignalKill = 9;
    internal const int SignalStop = 17;

    public static uint EffectiveUserId => geteuid();

    public static int ParentPid => getppid();

    public static MacProcessIdentity GetCurrent()
    {
        var status = TryGetIdentity(getpid(), out var identity, out var error);
        if (status != MacProbeStatus.Found)
        {
            throw new DockException("mac-spi-unavailable",
                "proc_pidinfo could not identify the current process: " + (error ?? "the process vanished") + ".");
        }
        return identity;
    }

    /// <summary>
    /// Queries the unique identifier and resource coalition of one process in
    /// two libproc calls. A process that exits between the calls is reported as
    /// <see cref="MacProbeStatus.Vanished"/>; every other failure is reported as
    /// <see cref="MacProbeStatus.Failed"/> with a bounded reason.
    /// </summary>
    public static MacProbeStatus TryGetIdentity(int pid, out MacProcessIdentity identity, out string? error)
    {
        identity = default;
        var status = TryGetVersion(pid, out var version, out error);
        if (status != MacProbeStatus.Found)
        {
            return status;
        }
        var buffer = new byte[CoalitionSize];
        var written = proc_pidinfo(pid, FlavorCoalition, 0, buffer, buffer.Length);
        if (written != CoalitionSize)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ErrnoNoSuchProcess)
            {
                error = null;
                return MacProbeStatus.Vanished;
            }
            error = $"proc_pidinfo(PROC_PIDCOALITIONINFO) for pid {pid} returned {written} (errno {errno}).";
            return MacProbeStatus.Failed;
        }
        identity = new MacProcessIdentity(pid, version, BitConverter.ToUInt64(buffer, CoalitionIdOffset));
        error = null;
        return MacProbeStatus.Found;
    }

    /// <summary>Queries only the pid version of one process.</summary>
    public static MacProbeStatus TryGetVersion(int pid, out uint version, out string? error)
    {
        version = 0;
        var buffer = new byte[UniqueIdentifierSize];
        var written = proc_pidinfo(pid, FlavorUniqueIdentifier, 0, buffer, buffer.Length);
        if (written != UniqueIdentifierSize)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ErrnoNoSuchProcess)
            {
                error = null;
                return MacProbeStatus.Vanished;
            }
            error = $"proc_pidinfo(PROC_PIDUNIQIDENTIFIERINFO) for pid {pid} returned {written} (errno {errno}).";
            return MacProbeStatus.Failed;
        }
        version = BitConverter.ToUInt32(buffer, PidVersionOffset);
        error = null;
        return MacProbeStatus.Found;
    }

    /// <summary>
    /// Re-checks that the recorded process still exists with the same pid
    /// version. A pid that was recycled reports <see cref="MacProbeStatus.Vanished"/>.
    /// </summary>
    public static MacProbeStatus CheckAlive(MacProcessIdentity identity, out string? error)
    {
        var status = TryGetVersion(identity.Pid, out var version, out error);
        if (status == MacProbeStatus.Found && version != identity.Version)
        {
            error = null;
            return MacProbeStatus.Vanished;
        }
        return status;
    }

    /// <summary>
    /// Lists the pids owned by the current effective uid through
    /// <c>proc_listpids(PROC_UID_ONLY, uid, ...)</c>. Bounded; an oversized or
    /// inconsistent process list is an error.
    /// </summary>
    public static bool TryListOwnUidPids(out int[] pids, out string? error)
    {
        pids = [];
        error = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = proc_listpids(ListOwnUidSelector, EffectiveUserId, null, 0);
            if (size <= 0)
            {
                error = $"proc_listpids(PROC_UID_ONLY) sizing failed (errno {Marshal.GetLastPInvokeError()}).";
                return false;
            }
            if (size / sizeof(int) > PidListLimit)
            {
                error = $"proc_listpids(PROC_UID_ONLY) reports more than {PidListLimit} processes; refusing to scan a truncated list.";
                return false;
            }
            var buffer = new byte[size + 4096];
            var written = proc_listpids(ListOwnUidSelector, EffectiveUserId, buffer, buffer.Length);
            if (written <= 0)
            {
                error = $"proc_listpids(PROC_UID_ONLY) failed (errno {Marshal.GetLastPInvokeError()}).";
                return false;
            }
            if (written > buffer.Length)
            {
                error = "proc_listpids(PROC_UID_ONLY) returned more bytes than the buffer holds.";
                return false;
            }
            // Confirm the list did not outgrow the buffer while it was read; a
            // truncated scan must never be mistaken for a complete one.
            var recheck = proc_listpids(ListOwnUidSelector, EffectiveUserId, null, 0);
            if (recheck > 0 && recheck <= buffer.Length)
            {
                var count = written / sizeof(int);
                var result = new int[count];
                for (var index = 0; index < count; index++)
                {
                    result[index] = BitConverter.ToInt32(buffer, index * sizeof(int));
                }
                pids = result;
                return true;
            }
        }
        error = "proc_listpids(PROC_UID_ONLY) kept reporting a growing process list.";
        return false;
    }

    /// <summary>
    /// Returns every own-uid process whose resource coalition equals
    /// <paramref name="coalitionId"/>, each with a freshly verified pid version.
    /// Processes that vanish during the scan are skipped with bounded work; any
    /// other query failure makes the whole scan fail so callers cannot mistake
    /// an unverifiable state for an empty coalition.
    /// </summary>
    public static bool TryScanCoalition(ulong coalitionId, out List<MacProcessIdentity> members, out string? error)
    {
        members = [];
        if (!TryListOwnUidPids(out var pids, out error))
        {
            return false;
        }
        var failures = new List<string>();
        foreach (var pid in pids)
        {
            var status = TryGetIdentity(pid, out var identity, out var detail);
            for (var retry = 0; retry < 2 && status == MacProbeStatus.Failed; retry++)
            {
                Thread.Sleep(20);
                status = TryGetIdentity(pid, out identity, out detail);
            }
            if (status == MacProbeStatus.Vanished)
            {
                continue;
            }
            if (status == MacProbeStatus.Failed)
            {
                AddFailure(failures, detail ?? $"pid {pid}");
                continue;
            }
            if (identity.CoalitionId == coalitionId)
            {
                members.Add(identity);
            }
        }
        if (failures.Count > 0)
        {
            error = $"identity queries failed for {failures.Count} process(es): {string.Join("; ", failures)}";
            members = [];
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Delivers one signal through the audit token; returns errno directly.</summary>
    public static int Signal(MacProcessIdentity identity, int signal)
    {
        var token = new int[8];
        token[5] = identity.Pid;
        token[7] = unchecked((int)identity.Version);
        return proc_signal_with_audittoken(token, signal);
    }

    /// <summary>
    /// Freezes then kills every process whose resource coalition equals
    /// <paramref name="coalitionId"/>, re-scanning until none remain. Only
    /// processes verified by fresh libproc queries are ever signalled, and each
    /// signal carries the full audit token so pid reuse cannot harm an
    /// unrelated process. Returns false, with a bounded reason, when the state
    /// cannot be verified as empty.
    /// </summary>
    public static bool TryStopCoalition(ulong coalitionId, out string? error, MacProcessIdentity? keep = null)
    {
        try
        {
            return StopCoalitionCore(coalitionId, out error, keep);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = "coalition cleanup failed: " + exception.Message;
            return false;
        }
    }

    private static bool StopCoalitionCore(ulong coalitionId, out string? error, MacProcessIdentity? keep)
    {
        error = null;
        if (coalitionId == 0)
        {
            error = "Refusing to stop coalition 0: the id does not identify an isolated coalition.";
            return false;
        }
        var failures = new List<string>();

        // Freeze: every coalition process that can fork must be stopped before
        // the kill sweep, otherwise a forking parent can keep producing members.
        var frozen = new Dictionary<int, uint>();
        for (var round = 0; round < MacJobControl.FreezeRounds; round++)
        {
            if (!Scan(out var members, out error))
            {
                return false;
            }
            if (members.Count == 0)
            {
                return true;
            }
            var newlyStopped = 0;
            foreach (var member in members)
            {
                if (frozen.TryGetValue(member.Pid, out var version) && version == member.Version)
                {
                    continue;
                }
                var errno = Signal(member, SignalStop);
                if (errno == 0)
                {
                    frozen[member.Pid] = member.Version;
                    newlyStopped++;
                }
                else if (errno != ErrnoNoSuchProcess)
                {
                    AddFailure(failures, $"SIGSTOP pid {member.Pid} failed with errno {errno}");
                }
            }
            if (newlyStopped == 0)
            {
                break;
            }
            Thread.Sleep(30);
        }

        // Kill: repeat SIGKILL sweeps until the coalition scans empty.
        for (var round = 0; round < MacJobControl.KillRounds; round++)
        {
            if (!Scan(out var members, out error))
            {
                return false;
            }
            if (members.Count == 0)
            {
                return true;
            }
            foreach (var member in members)
            {
                var errno = Signal(member, SignalKill);
                if (errno != 0 && errno != ErrnoNoSuchProcess)
                {
                    AddFailure(failures, $"SIGKILL pid {member.Pid} failed with errno {errno}");
                }
            }
            Thread.Sleep(100);
        }
        if (!Scan(out var remaining, out error))
        {
            return false;
        }
        if (remaining.Count == 0)
        {
            return true;
        }
        error = MacJobControl.Truncate(
            $"coalition {coalitionId} still contains {remaining.Count} process(es) after the kill sweeps"
            + (failures.Count > 0 ? "; " + string.Join("; ", failures) : ".")
            + "; the task is not reported stopped.",
            600);
        return false;

        bool Scan(out List<MacProcessIdentity> members, out string? detail)
        {
            if (!TryScanCoalition(coalitionId, out members, out detail)) return false;
            if (keep is { } keeper) members.RemoveAll(member => member == keeper);
            return true;
        }
    }

    private static void AddFailure(List<string> failures, string message)
    {
        if (failures.Count < FailureListLimit)
        {
            failures.Add(MacJobControl.Truncate(message, 400));
        }
    }

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, [Out] byte[] buffer, int buffersize);

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    private static extern int proc_listpids(uint type, uint typeinfo, byte[]? buffer, int buffersize);

    [DllImport("libSystem.B.dylib", EntryPoint = "proc_signal_with_audittoken")]
    private static extern int proc_signal_with_audittoken([In] int[] token, int signal);

    [DllImport("libSystem.B.dylib")]
    private static extern int getpid();

    [DllImport("libSystem.B.dylib")]
    private static extern int getppid();

    [DllImport("libSystem.B.dylib")]
    private static extern uint geteuid();
}

/// <summary>Kind of a filesystem entry as reported by <c>lstat</c>.</summary>
internal enum MacFileKind { Missing, Directory, RegularFile, SymbolicLink, Other }

/// <summary>
/// Minimal <c>lstat</c> probe: owner uid and entry kind only, never following
/// symlinks. The Darwin <c>struct stat</c> layout is stable for 64-bit: mode at
/// offset 4 (<c>uint16</c>), uid at offset 16 (<c>uint32</c>).
/// </summary>
internal static class MacFileIdentity
{
    private const int StatBufferSize = 144;
    private const int ModeOffset = 4;
    private const int OwnerUidOffset = 16;
    private const int ErrnoNoEntry = 2;
    private const int FileTypeMask = 0xF000;
    private const int FileTypeDirectory = 0x4000;
    private const int FileTypeRegular = 0x8000;
    private const int FileTypeSymbolicLink = 0xA000;

    /// <summary>
    /// Stat one path. Returns false only when the entry could not be inspected
    /// at all; a missing entry returns true with <see cref="MacFileKind.Missing"/>.
    /// </summary>
    public static bool TryStat(string path, out MacFileKind kind, out uint ownerUid, out string? error)
    {
        kind = MacFileKind.Other;
        ownerUid = 0;
        error = null;
        var buffer = new byte[StatBufferSize];
        if (lstat(path, buffer) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ErrnoNoEntry)
            {
                kind = MacFileKind.Missing;
                return true;
            }
            error = $"lstat('{path}') failed with errno {errno}.";
            return false;
        }
        ownerUid = BitConverter.ToUInt32(buffer, OwnerUidOffset);
        var mode = BitConverter.ToUInt16(buffer, ModeOffset);
        kind = (mode & FileTypeMask) switch
        {
            FileTypeDirectory => MacFileKind.Directory,
            FileTypeRegular => MacFileKind.RegularFile,
            FileTypeSymbolicLink => MacFileKind.SymbolicLink,
            _ => MacFileKind.Other,
        };
        return true;
    }

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    private static extern int lstat(string path, [Out] byte[] buffer);
}
