using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDock.Core;

[SupportedOSPlatform("windows")]
internal static class WindowsHostLauncher
{
    public static void Start(string executable, string home)
    {
        try { StartCore(Path.GetFullPath(executable), Path.GetFullPath(home)); }
        catch (Win32Exception)
        {
            throw new DockException("HostLaunchFailed", "无法在当前 Windows 桌面会话独立启动后台。请确认已登录桌面且系统策略允许启动。");
        }
    }

    private static void StartCore(string executable, string home)
    {
        using var caller = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (IsTokenRestricted(caller.AccessToken) || TokenInt(caller.AccessToken, 29) != 0)
            throw new DockException("UntrustedClient", "任务沙箱不能启动后台。");
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero || GetWindowThreadProcessId(shellWindow, out var shellPid) == 0)
            throw new DockException("HostLaunchFailed", "未找到 Windows 桌面会话。请先登录桌面，再启动后台。");
        using var shell = OpenProcess(0x1080, false, shellPid); // QUERY_LIMITED_INFORMATION | CREATE_PROCESS
        if (shell.IsInvalid || !OpenProcessToken(shell, 0x0008, out var token)) throw new Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var session = TokenInt(caller.AccessToken, 12);
            if (caller.User is null || caller.User != identity.User || session == 0 || session != TokenInt(token, 12) ||
                IsTokenRestricted(token) || TokenInt(token, 20) != 0 || TokenInt(token, 29) != 0 ||
                Integrity(caller.AccessToken) < Integrity(token))
                throw new DockException("UntrustedClient", "后台只能由同一用户的普通 Windows 桌面会话启动。");
        }
        if (!IsProcessInJob(shell, IntPtr.Zero, out var inJob) || inJob) throw new Win32Exception();

        var bytes = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        if (bytes == IntPtr.Zero) throw new Win32Exception();
        var attributes = Marshal.AllocHGlobal(bytes);
        var parent = Marshal.AllocHGlobal(IntPtr.Size);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception();
            initialized = true;
            Marshal.WriteIntPtr(parent, shell.DangerousGetHandle());
            // Inherit the desktop shell's lifetime, not the client's kill-on-close Job or stdio.
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020000, parent,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            var command = new StringBuilder($"{QuotePath(executable)} --host --home {QuotePath(home)}");
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08080000,
                    IntPtr.Zero, null, ref startup, out var process)) throw new Win32Exception();
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
            GC.KeepAlive(shell);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(parent);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static string QuotePath(string path)
    {
        if (path.Contains('"')) throw new ArgumentException("路径包含无效字符。", nameof(path));
        return "\"" + path + new string('\\', path.Length - path.TrimEnd('\\').Length) + "\"";
    }

    private static int TokenInt(SafeAccessTokenHandle token, int kind)
    {
        if (!GetTokenInformation(token, kind, out var value, sizeof(int), out _)) throw new Win32Exception();
        return value;
    }

    private static int Integrity(SafeAccessTokenHandle token)
    {
        GetTokenBuffer(token, 25, IntPtr.Zero, 0, out var size);
        if (size <= 0) throw new Win32Exception();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenBuffer(token, 25, buffer, size, out _)) throw new Win32Exception();
            return int.Parse(new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value.Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill;
        public uint Flags;
        public short Show, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int needed);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetTokenBuffer(SafeAccessTokenHandle token, int kind, IntPtr value, int size, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
