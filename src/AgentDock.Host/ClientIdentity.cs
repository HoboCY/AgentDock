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
        if (!OperatingSystem.IsWindows()) return;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) Deny();
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) Deny();
        else using (token) if (IsTokenRestricted(token)) Deny();
    }
    private static void Deny() => throw new DockException("UntrustedClient", "任务沙箱不能调用后台管理接口。");
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
