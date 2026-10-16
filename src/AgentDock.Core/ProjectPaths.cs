namespace AgentDock.Core;

public static class ProjectPaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new DockException("InvalidProject", "工作目录必须是现有的绝对目录。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Reject aliases instead of allowing different spellings to bypass a project write lock.
        for (var folder = new DirectoryInfo(full); folder is not null; folder = folder.Parent)
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new DockException("ProjectAlias", "项目路径及父目录不能是符号链接或目录联接，请使用实际路径。");
        return full;
    }

    public static bool Overlap(string first, string second) => Within(first, second) || Within(second, first);
    public static bool Within(string path, string root) => path.Equals(root, Comparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, Comparison);
}
