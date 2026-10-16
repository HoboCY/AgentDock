using System.IO.Pipes;
using System.Runtime.InteropServices;
using AgentDock.Core;
using Microsoft.Win32.SafeHandles;

namespace AgentDock.Host;

internal static class ClientIdentity
{
    // The task sandbox shares the user SID. CurrentUserOnly alone must not let a
    // restricted task process reconfigure the privileged host or launch another task.
    public static void RequireUnrestrictedClient(NamedPipeServerStream pipe)
    {
        if (OperatingSystem.IsMacOS())
        {
            // LOCAL_PEERTOKEN binds the kernel identity to this connection,
            // including pidversion; an exited/reused PID cannot change its trust.
            uint size = 32;
            if (GetPeerAuditToken(pipe.SafePipeHandle, 0, 6, out var peer, ref size) != 0 ||
                size != 32 || peer.Pid == 0 || CheckSandbox(peer, IntPtr.Zero, 0) != 0)
                Deny();
            return;
        }
        if (!OperatingSystem.IsWindows()) Deny();
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) Deny();
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) Deny();
        else using (token) if (IsTokenRestricted(token)) Deny();
    }
    private static void Deny() => throw new DockException("UntrustedClient", "任务沙箱不能调用后台管理接口。");
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetPeerAuditToken(SafePipeHandle socket, int level, int option,
        out AuditToken value, ref uint length);
    [DllImport("/usr/lib/libsandbox.dylib", EntryPoint = "sandbox_check_by_audit_token")]
    private static extern int CheckSandbox(AuditToken token, IntPtr operation, int filter);
    [StructLayout(LayoutKind.Sequential)]
    private struct AuditToken
    {
        public uint AuditUser, EffectiveUser, EffectiveGroup, RealUser, RealGroup, Pid, AuditSession, PidVersion;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
}
