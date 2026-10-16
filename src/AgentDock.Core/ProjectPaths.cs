using System.Runtime.InteropServices;

namespace AgentDock.Core;

public static class ProjectPaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new DockException("InvalidProject", "工作目录必须是现有的绝对目录。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Native realpath also restores the stored casing on APFS. Resolving
        // aliases once at admission gives all spellings the same project lock.
        if (OperatingSystem.IsMacOS()) return Canonicalize(full);
        // Reject aliases instead of allowing different spellings to bypass a project write lock.
        for (var folder = new DirectoryInfo(full); folder is not null; folder = folder.Parent)
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new DockException("ProjectAlias", "项目路径及父目录不能是符号链接或目录联接，请使用实际路径。");
        return full;
    }

    public static string Canonicalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsMacOS()) return full;
        if (!Directory.Exists(full))
        {
            var parent = Path.GetDirectoryName(full);
            if (parent is null) throw new IOException("无法解析目录的实际路径。");
            return Path.Combine(Canonicalize(parent), Path.GetFileName(full));
        }
        var pointer = RealPath(full, IntPtr.Zero);
        if (pointer == IntPtr.Zero) throw new IOException("无法解析目录的实际路径。");
        try { return Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("目录实际路径为空。"); }
        finally { Free(pointer); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);

    public static bool Overlap(string first, string second) => Within(first, second) || Within(second, first);
    public static bool Within(string path, string root) => path.Equals(root, Comparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, Comparison);
}
