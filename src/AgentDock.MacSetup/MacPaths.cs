using AgentDock.Core;
using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// macOS 稳定路径契约（全部从用户主目录 / 安装根推导，不硬编码用户名）：
///   &lt;root&gt;/AgentDock.app                                  稳定启动器 .app（本安装私有，跨版本有效）
///   &lt;root&gt;/AgentDock.app/Contents/MacOS/AgentDock         启动器可执行文件
///   ~/Applications/AgentDock.app                           可选 shell 集成链接（仅在目标缺失或已指向本安装时创建）
/// 登录自启不在这里定义：统一走 Core 的 MacLoginStartup（按 home 派生 plist，默认不写）。
/// </summary>
internal static class MacPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string UserApplications => Path.Combine(Home, "Applications");
    /// <summary>可选的用户级 .app 链接；绝不替换无关的既有应用。</summary>
    public static string UserApplicationLink => Path.Combine(UserApplications, "AgentDock.app");

    /// <summary>从进程可执行文件向上查找所属 .app 包；不在包内（例如 dotnet 开发输出）时返回 null。</summary>
    public static string? FindContainingBundle(string? processPath = null)
    {
        var path = processPath ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return null;
        var current = Path.GetDirectoryName(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(current, "Contents", "Info.plist")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    /// <summary>
    /// 未显式 --root 时的根推导：稳定启动器位于 &lt;root&gt;/AgentDock.app，其父目录必须带
    /// install/current.json 才算已安装根；否则返回 null 由调用方使用默认根（安装器场景）。
    /// </summary>
    public static string? InferRootFromBundle()
    {
        var bundle = FindContainingBundle();
        if (bundle is null) return null;
        var root = Path.GetDirectoryName(Path.GetFullPath(bundle));
        if (root is null) return null;
        return File.Exists(Path.Combine(root, "install", "current.json")) ? root : null;
    }

    /// <summary>条目存在（包括已断开的符号链接）时返回 true；绝不跟随链接判断目标。</summary>
    public static bool EntryExists(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) return true;
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>符号链接（重解析点）且解析后的目标与期望路径一致。</summary>
    public static bool IsLinkPointingTo(string linkPath, string target)
    {
        try
        {
            var info = new DirectoryInfo(linkPath);
            var linkTarget = info.LinkTarget;
            if (string.IsNullOrEmpty(linkTarget)) return false;
            var resolved = Path.IsPathFullyQualified(linkTarget)
                ? linkTarget
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(linkPath))!, linkTarget);
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
                ProjectPaths.Comparison);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>路径是真实的目录/文件（不是符号链接）时返回 true。</summary>
    public static bool IsRealEntry(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return false;
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    /// <summary>path 是 root 的直接子项（不含 root 自身）。</summary>
    public static bool IsDirectChild(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative is "." or "") return false;
        return !relative.StartsWith("..", StringComparison.Ordinal) &&
            relative.IndexOf(Path.DirectorySeparatorChar) < 0 && relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
    }

    /// <summary>path 位于 root 之下（含任意深度，不含 root 自身）。</summary>
    public static bool IsUnder(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(fullRoot, ProjectPaths.Comparison);
    }
}
