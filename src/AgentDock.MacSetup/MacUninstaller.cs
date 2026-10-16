using AgentDock.Core;
using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// 显式卸载（--uninstall）：默认保留 &lt;root&gt;/data（任务数据、Keychain 引用都由 Host 管理，不动）。
/// 只删除经校验属于本安装的目标：current.json 指向的版本目录 + install/ 下可确认的版本目录、
/// &lt;root&gt;/plugin、&lt;root&gt;/AgentDock.app、~/Applications 中指向本安装的链接、本安装的登录自启配置。
/// 目标是指向别处的链接/重解析点或无法确认属于本安装时一律跳过；绝不删除安装根或无关文件。
/// 全程在 OperationGate 内执行：更新标记存活、后台有未完成任务、launch.lock 被占时拒绝卸载。
/// </summary>
internal static class MacUninstaller
{
    public static int Run(InstallLayout layout, Action<string> log)
    {
        CurrentInstall? current = null;
        try
        {
            current = layout.ReadCurrent();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("当前安装记录损坏；请先修复安装，再卸载。未删除文件。", ex);
        }
        if (current is null || current.Schema != 1 ||
            !InstallLayout.SamePath(current.DataHome, layout.DataDir) ||
            !MacPaths.IsDirectChild(layout.InstallDir, current.Root) ||
            !MacOwnership.IsInstalledVersion(current.Root))
            throw new InvalidDataException("此目录没有有效的 AgentDock 安装记录，拒绝卸载。未删除文件。");

        var errors = new List<string>();
        using (var gate = MacOperationGate.Enter(layout, current?.Version ?? "unknown", "uninstall", log))
        {
            RemoveVersionDirs(layout, current, errors, log);
            RemoveOwnedDirectory(layout.StablePluginDir, "稳定插件源", errors, log);
            RemoveOwnedDirectory(layout.StableAppDir, "稳定启动器", errors, log);
            if (OperatingSystem.IsMacOS())
            {
                RemoveUserApplicationLink(layout, errors, log);
                RemoveLoginRegistration(layout, errors, log);
            }
            if (errors.Count == 0)
            {
                TryDeleteFile(layout.CurrentManifestPath, "当前版本指针", errors, log);
                TryDeleteFile(layout.MachinePath, "本机信息", errors, log);
            }
            gate.Completed = errors.Count == 0;
        }
        CleanupInstallDir(layout, log);

        if (errors.Count > 0)
        {
            foreach (var error in errors) log("卸载未完成：" + error);
            return 1;
        }
        log($"卸载完成；用户数据已保留：{layout.DataDir}");
        return 0;
    }

    /// <summary>删除 current.json 指向的版本目录，以及 install/ 下可确认属于本安装的旧版本目录。</summary>
    private static void RemoveVersionDirs(
        InstallLayout layout, CurrentInstall? current, List<string> errors, Action<string> log)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (current is not null && current.Root is { Length: > 0 } recorded)
        {
            if (!MacPaths.IsDirectChild(layout.InstallDir, recorded) || !MacPaths.IsUnder(layout.InstallDir, recorded))
            {
                errors.Add($"当前版本目录越界（不在 install/ 下），拒绝删除：{recorded}");
            }
            else if (!MacOwnership.IsInstalledVersion(recorded))
            {
                errors.Add($"当前版本缺少有效安装记录，已保留：{recorded}");
            }
            else
            {
                seen.Add(Path.GetFullPath(recorded));
                RemoveOwnedDirectory(recorded, "版本目录", errors, log);
            }
        }
        if (!Directory.Exists(layout.InstallDir)) return;
        foreach (var directory in Directory.EnumerateDirectories(layout.InstallDir))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.')) continue;
            var full = Path.GetFullPath(directory);
            if (!seen.Add(full)) continue;
            if (!MacOwnership.IsInstalledVersion(full))
            {
                log($"保留无法确认属于 AgentDock 的目录：{full}");
                continue;
            }
            RemoveOwnedDirectory(full, "版本目录", errors, log);
        }
    }

    private static void RemoveOwnedDirectory(string path, string label, List<string> errors, Action<string> log)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                errors.Add($"{label}是符号链接/重解析点，拒绝跟随删除：{path}");
                return;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"无法检查{label}：{path}（{ex.Message}）");
            return;
        }
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else File.Delete(path);
            log($"已删除{label}：{path}");
        }
        catch (Exception ex)
        {
            errors.Add($"{label}删除失败：{path}（{ex.Message}）");
        }
    }

    private static void TryDeleteFile(string path, string label, List<string> errors, Action<string> log)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
            log($"已删除{label}：{path}");
        }
        catch (Exception ex)
        {
            errors.Add($"{label}删除失败：{path}（{ex.Message}）");
        }
    }

    /// <summary>只删除指向本安装根 .app 的 ~/Applications 链接；其他应用一律保留。</summary>
    private static void RemoveUserApplicationLink(InstallLayout layout, List<string> errors, Action<string> log)
    {
        var link = MacPaths.UserApplicationLink;
        if (!MacPaths.EntryExists(link))
        {
            return;
        }
        if (!MacPaths.IsLinkPointingTo(link, layout.StableAppDir))
        {
            log($"~/Applications/AgentDock.app 不属于本安装，保留：{link}");
            return;
        }
        try
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            else File.Delete(link);
            log($"已删除 ~/Applications 链接：{link}");
        }
        catch (Exception ex)
        {
            errors.Add($"~/Applications 链接删除失败：{link}（{ex.Message}）");
        }
    }

    /// <summary>只删除本安装 home 对应的 LaunchAgent 配置（Core 按 home 派生唯一路径）。</summary>
    private static void RemoveLoginRegistration(InstallLayout layout, List<string> errors, Action<string> log)
    {
        try
        {
            MacLoginStartup.Configure(layout.DataDir, false);
            var statePath = Path.Combine(layout.DataDir, "tasks.json");
            if (File.Exists(statePath))
            {
                var state = AtomicJson.Read<TaskService.ServiceState>(statePath);
                if (state.Configuration.StartAtLogin)
                {
                    state.Configuration = state.Configuration with { StartAtLogin = false };
                    AtomicJson.Write(statePath, state);
                }
            }
            log("已移除本安装的登录自启配置（如有）。");
        }
        catch (Exception ex)
        {
            errors.Add("登录自启配置清理失败：" + ex.Message);
        }
    }

    /// <summary>门禁释放后：清理 install/ 内本安装的工作目录残留，空目录才删除。</summary>
    private static void CleanupInstallDir(InstallLayout layout, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(layout.InstallDir)) return;
            // Unknown staging/backup directories are evidence, not deletion authority.
            var logFile = Path.Combine(layout.InstallDir, "setup.log");
            if (File.Exists(logFile)) File.Delete(logFile);
            if (!Directory.EnumerateFileSystemEntries(layout.InstallDir).Any())
            {
                Directory.Delete(layout.InstallDir);
                log("已删除 install 目录。");
            }
            else
            {
                log("install 目录仍含未知文件，已保留：" + layout.InstallDir);
            }
        }
        catch (Exception ex)
        {
            log($"install 清理提示：{ex.Message}");
        }
    }
}
