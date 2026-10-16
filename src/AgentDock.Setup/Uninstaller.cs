using System.Diagnostics;

namespace AgentDock.Setup;

internal static class Uninstaller
{
    /// <summary>
    /// 只删除 AgentDock 自己的程序文件与集成入口，保留用户数据目录。
    /// 与安装/更新共用同一门禁：更新标记 + 离线任务检查 + launch.lock，
    /// 后台运行中或存在未完成任务时拒绝卸载；不得绕过更新标记直接在活跃后台旁删除文件。
    /// </summary>
    public static void Uninstall(InstallLayout layout, bool noShellIntegration, Action<string> log)
    {
        if (layout.ReadCurrent() is null) throw new InvalidOperationException("此目录没有 AgentDock 安装记录，拒绝删除文件。");
        using (var gate = OperationGate.Enter(layout, "-", "uninstall", log))
        {
            log($"移除程序文件（保留用户数据：{layout.DataDir}）...");
            if (Directory.Exists(layout.InstallDir)) Directory.Delete(layout.InstallDir, true);
            if (Directory.Exists(layout.StablePluginDir)) Directory.Delete(layout.StablePluginDir, true);
            var self = Environment.ProcessPath;
            if (self is not null && File.Exists(layout.LauncherPath) && !InstallLayout.SamePath(self, layout.LauncherPath))
            {
                try
                {
                    File.Delete(layout.LauncherPath);
                }
                catch (Exception ex)
                {
                    log($"稳定启动器删除失败：{ex.Message}");
                }
            }
            if (!noShellIntegration)
            {
                ShellIntegration.Unregister(layout);
                ShellIntegration.RemoveShortcut();
            }
            gate.Completed = true;
        }
        ScheduleSelfDelete(layout, log);
    }

    public static void ScheduleSelfDelete(InstallLayout layout, Action<string> log)
    {
        var self = Environment.ProcessPath;
        if (self is null) return;
        var selfFull = Path.GetFullPath(self);
        var rootPrefix = Path.GetFullPath(layout.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!selfFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            log("当前进程不在安装根目录内（可能是下载的安装程序），跳过自清理。");
            return;
        }
        try
        {
            var copy = Path.Combine(Path.GetTempPath(), $"AgentDock-uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(self, copy, true);
            Process.Start(new ProcessStartInfo(copy, $"--cleanup-self \"{self}\" --wait-pid {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.Dispose();
            log("已安排移除稳定启动器。");
        }
        catch (Exception ex)
        {
            log($"自清理安排失败（可手动删除）：{ex.Message}");
        }
    }

    /// <summary>等待主进程退出后删除与辅助程序哈希相同的稳定启动器；文件仍占用时保留。</summary>
    public static void CleanupSelf(string target, int pid)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate cleanup helper.");
        target = Path.GetFullPath(target);
        if (!Path.GetFileName(target).Equals("AgentDock.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(target) || Payload.Sha256File(target) != Payload.Sha256File(self)) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(30000);
        }
        catch
        {
            // 进程已退出。
        }

        for (var i = 0; i < 40; i++)
        {
            try
            {
                if (!File.Exists(target)) return;
                File.Delete(target);
                if (!File.Exists(target)) return;
            }
            catch
            {
                // 文件仍被占用，稍后重试。
            }
            Thread.Sleep(250);
        }

        // A locked executable stays on disk for explicit manual removal.
    }
}
