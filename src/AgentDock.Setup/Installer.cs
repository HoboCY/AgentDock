using System.Diagnostics;

namespace AgentDock.Setup;

internal sealed record InstallOutcome(string Version, string VersionDir, string[] Warnings, bool AlreadyInstalled);

internal static class Installer
{
    public static InstallOutcome Install(InstallLayout layout, string? payloadArg, bool noShellIntegration, Action<string> log)
    {
        var payloadPath = ResolvePayloadPath(payloadArg);
        log($"安装源：{payloadPath}");
        var manifest = Payload.ReadManifest(payloadPath);
        ValidateForInstall(manifest);

        var current = layout.ReadCurrent();
        if (current is not null && !string.Equals(current.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            log($"检测到已安装版本 {current.Version}，按更新流程处理到 {manifest.Version}。");
            return Updater.ApplyUpdate(layout, payloadPath, manifest, noShellIntegration, log);
        }

        // 全新安装或同版本修复：与更新/卸载相同的门禁（更新标记 + 离线任务检查 + launch.lock），
        // 防止在存在未完成任务或后台运行时替换程序文件。
        using var gate = OperationGate.Enter(layout, manifest.Version, current is null ? "install" : "repair", log);
        var (versionDir, reused) = StageAndPlace(layout, payloadPath, manifest, log);
        var warnings = FinalizeArtifacts(layout, versionDir, manifest, noShellIntegration, log);
        gate.Completed = true;
        layout.Log($"安装完成：{manifest.Version}（{(reused ? "已存在，修复刷新" : "新展开")}）");
        return new InstallOutcome(manifest.Version, versionDir, warnings, reused);
    }

    public static void ValidateForInstall(PayloadManifest manifest)
    {
        if (manifest.Partial) throw new InvalidDataException("此安装包不完整（partial），不能作为安装或更新使用。");
        if (!string.Equals(manifest.Runtime, "win-x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"此安装程序只接受 win-x64 payload（当前为 {manifest.Runtime}）。");
        }
        var files = manifest.Files.Select(f => Payload.Normalize(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { "host/AgentDock.Host.exe", "desktop/AgentDock.Desktop.exe", "engine/AgentDock.Engine.exe",
            "engines/opencode/opencode.exe", "engines/codex/bin/codex.exe", "engines/codex/codex-path/rg.exe", "engines/git/usr/bin/sh.exe" })
            if (!files.Contains(required)) throw new InvalidDataException("安装包缺少必要组件：" + required);
    }

    /// <summary>解包到 staging 后放入版本目录；已存在且逐文件一致时复用，不一致则拒绝覆盖。</summary>
    public static (string VersionDir, bool Reused) StageAndPlace(
        InstallLayout layout, string payloadPath, PayloadManifest manifest, Action<string> log)
    {
        Directory.CreateDirectory(layout.InstallDir);
        var versionDir = layout.VersionDir(manifest.Version);
        if (Directory.Exists(versionDir))
        {
            if (!Payload.DirectoryMatches(versionDir, manifest))
            {
                throw new InvalidOperationException($"版本目录已存在且内容不一致：{versionDir}。请手动清理该目录后重试。");
            }
            log($"版本 {manifest.Version} 已存在且校验一致，跳过展开。");
            return (versionDir, true);
        }

        var staging = Path.Combine(layout.InstallDir, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            log("校验并展开 payload...");
            if (Directory.Exists(payloadPath))
            {
                Payload.CopyDirectory(payloadPath, staging);
            }
            else
            {
                Payload.ExtractZip(payloadPath, staging);
            }
            MoveDirectory(staging, versionDir);
            log($"已展开到 {versionDir}");
            return (versionDir, false);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>写入 current.json、稳定插件源、稳定启动器、本机信息、快捷方式与卸载入口。</summary>
    public static string[] FinalizeArtifacts(
        InstallLayout layout, string versionDir, PayloadManifest manifest, bool noShellIntegration, Action<string> log)
    {
        var current = new CurrentInstall(
            Schema: 1,
            Version: manifest.Version,
            Root: versionDir,
            Host: Path.Combine(versionDir, "host", "AgentDock.Host.exe"),
            Desktop: Path.Combine(versionDir, "desktop", "AgentDock.Desktop.exe"),
            Engine: Path.Combine(versionDir, "engine", "AgentDock.Engine.exe"),
            Plugin: Path.Combine(versionDir, "plugin"),
            DataHome: layout.DataDir);

        var warnings = new List<string>();
        CheckComponent(current.Host, "host/AgentDock.Host.exe", warnings);
        CheckComponent(current.Desktop, "desktop/AgentDock.Desktop.exe", warnings);
        CheckComponent(current.Engine, "engine/AgentDock.Engine.exe", warnings);
        RefreshStablePlugin(layout, current, warnings, log);

        try
        {
            EnsureLauncher(layout, log);
        }
        catch (Exception ex)
        {
            warnings.Add($"稳定启动器更新失败（可能正在运行）：{ex.Message}");
        }

        var machine = MachineProbe.Run(layout, versionDir, warnings);
        layout.WriteMachine(machine);
        if (!noShellIntegration)
        {
            try
            {
                ShellIntegration.Register(layout, manifest.Version);
            }
            catch (Exception ex)
            {
                warnings.Add($"快捷方式/卸载入口写入失败：{ex.Message}");
            }
        }
        foreach (var warning in warnings) layout.Log("警告：" + warning);
        // Publish the new version only after preparing its plugin and machine metadata.
        layout.WriteCurrent(current);
        return warnings.ToArray();
    }

    public static void RefreshStablePlugin(InstallLayout layout, CurrentInstall current, List<string> warnings, Action<string> log)
    {
        var source = current.Plugin;
        if (!Directory.Exists(source))
        {
            warnings.Add("安装包未包含插件源（plugin/），Codex 接入暂不可用。");
            return;
        }
        var staged = Path.Combine(layout.InstallDir, ".plugin-" + Guid.NewGuid().ToString("N"));
        CopyTree(source, staged);

        var runtimeDir = Path.Combine(staged, "plugins", "agentdock", "runtime");
        Directory.CreateDirectory(runtimeDir);
        var pluginHost = Path.Combine(runtimeDir, "AgentDock.Host.exe");
        if (!File.Exists(pluginHost) && File.Exists(current.Host))
        {
            File.Copy(current.Host, pluginHost);
            log("已把 Host 副本注入插件 runtime/。");
        }
        if (!File.Exists(pluginHost))
        {
            warnings.Add("插件 runtime 缺少 AgentDock.Host.exe（partial 构建），插件暂时无法启动。");
        }

        var install = new PluginInstall(
            Schema: 1,
            Product: "AgentDock",
            Version: current.Version,
            InstallRoot: layout.Root,
            HostManifest: layout.CurrentManifestPath,
            Launcher: layout.LauncherPath,
            DataHome: layout.DataDir);
        layout.WriteJsonAtomic(Path.Combine(runtimeDir, "install.json"), install);
        var backup = Path.Combine(layout.InstallDir, ".plugin-backup-" + Guid.NewGuid().ToString("N"));
        var movedOld = false;
        try
        {
            if (Directory.Exists(layout.StablePluginDir)) { MoveDirectory(layout.StablePluginDir, backup); movedOld = true; }
            MoveDirectory(staged, layout.StablePluginDir);
        }
        catch
        {
            if (movedOld && !Directory.Exists(layout.StablePluginDir)) MoveDirectory(backup, layout.StablePluginDir);
            TryDelete(staged);
            throw;
        }
        TryDelete(backup); // An old MCP proxy may still hold its executable; preserve that backup until it exits.
        log($"已刷新插件源：{layout.StablePluginDir}");
    }

    public static void EnsureLauncher(InstallLayout layout, Action<string> log)
    {
        var self = Environment.ProcessPath;
        if (self is null)
        {
            log("无法定位安装程序自身路径，跳过稳定启动器。");
            return;
        }
        if (InstallLayout.SamePath(self, layout.LauncherPath)) return;
        var name = Path.GetFileName(self);
        if (!name.StartsWith("AgentDock", StringComparison.OrdinalIgnoreCase))
        {
            log("当前进程不是 AgentDock 安装程序，跳过稳定启动器复制。");
            return;
        }
        File.Copy(self, layout.LauncherPath, overwrite: true);
        log($"稳定启动器：{layout.LauncherPath}");
    }

    public static void Launch(InstallLayout layout, bool tray = false)
    {
        var current = layout.ReadCurrent() ?? throw new InvalidOperationException("尚未安装 AgentDock。请先运行安装程序。");
        if (!File.Exists(current.Desktop))
        {
            throw new InvalidOperationException($"桌面程序缺失：{current.Desktop}。请重新安装完整安装包。");
        }
        var start = new ProcessStartInfo(current.Desktop)
        {
            WorkingDirectory = Path.GetDirectoryName(current.Desktop)!,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--home");
        start.ArgumentList.Add(layout.DataDir);
        if (tray) start.ArgumentList.Add("--tray");
        Process.Start(start)?.Dispose();
    }

    public static string? FindPayload(string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? SetupDirectory();
        var plain = Path.Combine(baseDir, "payload.zip");
        if (File.Exists(plain)) return plain;
        var zips = Directory.GetFiles(baseDir, "AgentDock-*-win-x64.zip");
        if (zips.Length > 0)
        {
            return zips.OrderByDescending(File.GetLastWriteTimeUtc).First();
        }
        var dir = Path.Combine(baseDir, "payload");
        return Directory.Exists(dir) ? dir : null;
    }

    public static string ResolvePayloadPath(string? payloadArg)
    {
        if (payloadArg is { Length: > 0 })
        {
            var full = Path.GetFullPath(payloadArg);
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                throw new FileNotFoundException($"找不到安装包：{full}");
            }
            return full;
        }
        return FindPayload() ?? throw new InvalidOperationException(
            "未找到安装包。请把 payload.zip（或 payload/ 目录）放在安装程序旁边，或用 --payload 指定路径。");
    }

    public static string SetupDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null &&
            !Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(processPath)!;
        }
        return AppContext.BaseDirectory;
    }

    private static void CheckComponent(string path, string display, List<string> warnings)
    {
        if (!File.Exists(path))
        {
            warnings.Add($"安装包缺少 {display}（partial 构建）；该组件不可用，请使用完整安装包。");
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void MoveDirectory(string source, string destination)
    {
        // Executable scanning can briefly hold freshly copied files on Windows.
        // Retry only sharing/access conflicts; a persistent denial still fails the transaction.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (Exception error) when (attempt < 12 && error is IOException or UnauthorizedAccessException &&
                (error.HResult & 0xffff) is 5 or 32 or 33)
            { Thread.Sleep(150); }
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败留给下次安装处理。
        }
    }
}
