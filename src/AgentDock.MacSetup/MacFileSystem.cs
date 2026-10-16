namespace AgentDock.MacSetup;

/// <summary>macOS 安装流程的文件操作：保留 Unix 权限位、拒绝符号链接、可排除安装器专属文件。</summary>
internal static class MacFileSystem
{
    /// <summary>
    /// 递归复制目录；<paramref name="excludeRelative"/> 以 payload 风格相对路径识别被排除的条目（含其子树）。
    /// 自实现遍历：遇到符号链接/联接等重解析条目立即拒绝，绝不进入（避免越界与循环）。
    /// </summary>
    public static void CopyTree(string source, string destination, Func<string, bool>? excludeRelative = null)
    {
        Directory.CreateDirectory(destination);
        var pending = new Stack<(DirectoryInfo Directory, string Relative)>();
        pending.Push((new DirectoryInfo(source), ""));
        while (pending.Count > 0)
        {
            var (directory, baseRelative) = pending.Pop();
            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"安装源含符号链接或其他重解析条目：{info.FullName}");
                }
                var relative = baseRelative.Length == 0 ? info.Name : baseRelative + "/" + info.Name;
                if (IsExcluded(relative, excludeRelative)) continue;
                var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                if (info is DirectoryInfo child)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((child, relative));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(info.FullName, target, overwrite: true);
                PreserveMode(info.FullName, target);
            }
        }
    }

    public static void PreserveMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch (Exception) when (!OperatingSystem.IsMacOS())
        {
            // 非 macOS 的 Unix 测试环境允许保留默认权限。
        }
    }

    public static void MoveDirectory(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 12 && error is IOException or UnauthorizedAccessException &&
                (error.HResult & 0xffff) is 5 or 32 or 33 or 66)
            {
                Thread.Sleep(150);
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败留给下一次安装处理。
        }
    }

    private static bool IsExcluded(string relative, Func<string, bool>? excludeRelative)
    {
        if (excludeRelative is null) return false;
        if (excludeRelative(relative)) return true;
        // 目录被排除时其子树同样不得复制。
        var separator = relative.LastIndexOf('/');
        while (separator > 0)
        {
            relative = relative[..separator];
            if (excludeRelative(relative)) return true;
            separator = relative.LastIndexOf('/');
        }
        return false;
    }
}
