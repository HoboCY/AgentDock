using System.Runtime.InteropServices;

namespace AgentDock.Engine;

/// <summary>
/// Reads the existing logon SID of the current (sandbox restricted) token. The
/// pipe compatibility adapter uses it to give the pinned Bun runtime an output
/// pipe the restricted token can actually read. The token is never modified.
/// </summary>
internal static class WindowsToken
{
    private const int TokenGroups = 2;
    private const uint TokenQuery = 0x0008;
    private const uint SeGroupLogonIdMask = 0xC0000000;

    public static string? TryGetLogonSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var process = GetCurrentProcess();
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }
        try
        {
            GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
            {
                return null;
            }
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(token, TokenGroups, buffer, needed, out needed))
                {
                    return null;
                }
                var count = Marshal.ReadInt32(buffer);
                var offset = IntPtr.Size == 8 ? 8 : 4;
                var size = IntPtr.Size == 8 ? 16 : 8;
                var entries = buffer + offset;
                for (var index = 0; index < count; index++)
                {
                    var entry = entries + index * size;
                    var attributes = (uint)Marshal.ReadInt32(entry, IntPtr.Size);
                    if ((attributes & SeGroupLogonIdMask) != SeGroupLogonIdMask)
                    {
                        continue;
                    }
                    var sid = Marshal.ReadIntPtr(entry);
                    if (ConvertSidToStringSidW(sid, out var text) && text != IntPtr.Zero)
                    {
                        try
                        {
                            return Marshal.PtrToStringUni(text);
                        }
                        finally
                        {
                            LocalFree(text);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
