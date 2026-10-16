using AgentDock.Setup;

namespace AgentDock.MacSetup;

internal sealed record MacInstallOutcome(string Version, string VersionDir, string[] Warnings, bool AlreadyInstalled);

/// <summary>
/// macOS 安装 / 同版本修复 / 更新：与 Windows 安装程序共用 OperationGate（更新标记 + 离线任务检查 + launch.lock），
/// 任一失败都不切换 current.json。稳定启动器、插件源、machine.json 都在 current.json 发布成功后才提交；
/// 发布前任一步失败都会还原旧资产（损坏的版本目录同样先备份再修复，失败可回滚）。
/// </summary>
internal static class MacInstaller
{
    /// <summary>payload 中必须存在且带可执行位的组件（路径相对 payload 根，与 Build-MacPackage.py 契约一致）。</summary>
    public static readonly string[] RequiredExecutables =
    [
        "host/AgentDock.Host",
        "desktop/AgentDock.app/Contents/MacOS/AgentDock.Desktop",
        "engine/AgentDock.Engine",
        "engines/opencode/opencode",
        "engines/codex/bin/codex",
        "engines/git/bin/git"
    ];

    public static readonly string[] RequiredFiles =
    [
        "desktop/AgentDock.app/Contents/Info.plist",
        "plugin/.agents/plugins/marketplace.json",
        "plugin/plugins/agentdock/.codex-plugin/plugin.json",
        "plugin/plugins/agentdock/.mcp.json"
    ];

    public static MacInstallOutcome Install(InstallLayout layout, string? payloadArg, bool noShellIntegration, Action<string> log)
    {
        var payloadPath = ResolvePayloadPath(payloadArg);
        log($"安装源：{payloadPath}");
        var manifest = Payload.ReadManifest(payloadPath);
        ValidateForInstall(manifest);

        var current = layout.ReadCurrent();
        if (current is not null && !string.Equals(current.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            log($"检测到已安装版本 {current.Version}，按更新流程处理到 {manifest.Version}。");
            return ApplyUpdate(layout, payloadPath, manifest, noShellIntegration, log);
        }

        // 全新安装或同版本修复：与更新共用门禁（更新标记 + 离线任务检查 + launch.lock），
        // 防止在存在未完成任务或后台运行时替换程序文件。
        using var gate = MacOperationGate.Enter(layout, manifest.Version, current is null ? "install" : "repair", log);
        var staged = StageAndPlace(layout, payloadPath, manifest, log);
        try
        {
            var warnings = FinalizeArtifacts(layout, staged.VersionDir, manifest, noShellIntegration, log);
            staged.Repair?.Commit();
            gate.Completed = true;
            layout.Log($"安装完成：{manifest.Version}（{(staged.Reused ? "已存在，修复刷新" : "新展开")}）");
            return new MacInstallOutcome(manifest.Version, staged.VersionDir, warnings, staged.Reused);
        }
        catch
        {
            staged.Repair?.Rollback();
            throw;
        }
    }

    /// <summary>用户明确选择后应用的更新：目标版本必须与当前不同，其余门禁与安装一致。</summary>
    public static MacInstallOutcome ApplyUpdate(
        InstallLayout layout, string payloadPath, PayloadManifest manifest, bool noShellIntegration, Action<string> log)
    {
        ValidateForInstall(manifest);
        var current = layout.ReadCurrent() ?? throw new InvalidOperationException("尚未安装 AgentDock，无法应用更新。请先运行安装程序。");
        if (string.Equals(current.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"当前已是版本 {manifest.Version}，无需更新。");
        }

        var payloadSha256 = File.Exists(payloadPath) ? Payload.Sha256File(payloadPath) : null;
        using var gate = MacOperationGate.Enter(layout, manifest.Version, "update", log, payloadSha256);
        var staged = StageAndPlace(layout, payloadPath, manifest, log);
        try
        {
            var warnings = FinalizeArtifacts(layout, staged.VersionDir, manifest, noShellIntegration, log);
            staged.Repair?.Commit();
            gate.Completed = true;
            layout.Log($"更新完成：{current.Version} -> {manifest.Version}（{(staged.Reused ? "复用已校验版本目录" : "新展开")}）");
            return new MacInstallOutcome(manifest.Version, staged.VersionDir, warnings, staged.Reused);
        }
        catch
        {
            staged.Repair?.Rollback();
            throw;
        }
    }

    public static void ValidateForInstall(PayloadManifest manifest)
    {
        if (manifest.Partial) throw new InvalidDataException("此安装包不完整（partial），不能作为安装或更新使用。");
        if (!string.Equals(manifest.Runtime, "osx-arm64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"此安装程序只接受 osx-arm64 payload（当前为 {manifest.Runtime}）。");
        }

        // macOS 清单必须逐文件声明安全权限位；路径重复、缺 mode 或含 setuid/setgid/sticky 一律拒绝。
        var files = new Dictionary<string, PayloadFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalized = Payload.Normalize(file.Path);
            if (!files.TryAdd(normalized, file)) throw new InvalidDataException("安装包清单包含重复路径：" + normalized);
            if (file.Mode is null)
            {
                throw new InvalidDataException(
                    $"安装包缺少权限位声明（mode）：{normalized}。macOS payload 必须由 Build-MacPackage.py 生成。");
            }
            _ = Payload.ParseMode(file.Mode);
            if (file.Mode is not ("0644" or "0755"))
                throw new InvalidDataException($"macOS payload 只接受 0644 / 0755 权限：{normalized}");
        }
        foreach (var required in RequiredExecutables)
        {
            if (!files.TryGetValue(required, out var spec)) throw new InvalidDataException("安装包缺少必要组件：" + required);
            var mode = Payload.ParseMode(spec.Mode!);
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            {
                throw new InvalidDataException($"安装包可执行文件缺少可执行位：{required}");
            }
        }
        foreach (var required in RequiredFiles)
        {
            if (!files.ContainsKey(required)) throw new InvalidDataException("安装包缺少必要组件：" + required);
        }
    }

    internal sealed record StagedVersion(string VersionDir, bool Reused, PendingSwap? Repair);

    /// <summary>
    /// 解包到 staging 后放入版本目录；已存在且逐文件一致时复用。
    /// 同版本但内容不一致（损坏修复）时：先把损坏目录备份，再放入新目录，返回的回滚句柄在
    /// current.json 发布成功后释放/提交，失败则还原损坏目录——绝不原地覆盖或直接删除。
    /// </summary>
    public static StagedVersion StageAndPlace(
        InstallLayout layout, string payloadPath, PayloadManifest manifest, Action<string> log)
    {
        Directory.CreateDirectory(layout.InstallDir);
        var versionDir = layout.VersionDir(manifest.Version);
        if (Directory.Exists(versionDir))
        {
            if (Payload.DirectoryMatches(versionDir, manifest))
            {
                MacOwnership.RecordVersion(versionDir, manifest.Version);
                log($"版本 {manifest.Version} 已存在且校验一致，跳过展开。");
                return new StagedVersion(versionDir, true, null);
            }
            log($"版本目录内容与清单不一致，按损坏修复处理（先备份旧目录）：{versionDir}");
        }
        else if (File.Exists(versionDir))
        {
            log($"版本路径被非目录文件占用，按损坏修复处理（先备份）：{versionDir}");
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
            MacOwnership.RecordVersion(staging, manifest.Version);
            if (Directory.Exists(versionDir) || File.Exists(versionDir))
            {
                var repair = PendingSwap.Replace(staging, versionDir, layout.InstallDir);
                log($"已就位于 {versionDir}（旧版本目录已备份，待 current.json 发布后清理）");
                return new StagedVersion(versionDir, false, repair);
            }
            MacFileSystem.MoveDirectory(staging, versionDir);
            log($"已展开到 {versionDir}");
            return new StagedVersion(versionDir, false, null);
        }
        catch
        {
            MacFileSystem.TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// 事务化写入稳定启动器、稳定插件源、本机信息；全部成功后才原子切换 current.json。
    /// current.json 发布是唯一提交点：发布前任一步失败都会回滚启动器/插件/machine 并抛出；
    /// 发布后的 shell 集成只记警告，不再影响已提交的安装。
    /// </summary>
    public static string[] FinalizeArtifacts(
        InstallLayout layout, string versionDir, PayloadManifest manifest, bool noShellIntegration, Action<string> log)
    {
        var warnings = new List<string>();
        var current = new CurrentInstall(
            Schema: 1,
            Version: manifest.Version,
            Root: versionDir,
            Host: Path.Combine(versionDir, "host", "AgentDock.Host"),
            Desktop: Path.Combine(versionDir, "desktop", "AgentDock.app", "Contents", "MacOS", "AgentDock.Desktop"),
            Engine: Path.Combine(versionDir, "engine", "AgentDock.Engine"),
            Plugin: Path.Combine(versionDir, "plugin"),
            DataHome: layout.DataDir);

        var launcherPath = layout.StableLauncher;
        PendingSwap? appSwap = null;
        PendingSwap? pluginSwap = null;
        FileSnapshot? machineSnapshot = null;
        try
        {
            appSwap = MacStableApp.Prepare(layout, warnings, log);
            pluginSwap = MacPluginIntegration.Prepare(layout, current, launcherPath, warnings, log);
            machineSnapshot = FileSnapshot.Capture(layout.MachinePath);
            layout.WriteMachine(MacMachineProbe.Run(versionDir, warnings));
            // 提交点：只有到这里成功，才认为新版本生效。
            layout.WriteCurrent(current);
        }
        catch (Exception error)
        {
            MacRollback.Run(error,
                ("machine.json", () => machineSnapshot?.Restore()),
                ("插件源", () => pluginSwap?.Rollback()),
                ("稳定启动器", () => appSwap?.Rollback()));
            throw;
        }

        appSwap?.Commit();
        pluginSwap?.Commit();
        foreach (var warning in warnings) layout.Log("警告：" + warning);
        if (!noShellIntegration)
        {
            MacShellIntegration.Register(layout, warnings, log);
            foreach (var warning in warnings) layout.Log("警告：" + warning);
        }
        return warnings.ToArray();
    }

    public static string? FindPayload()
    {
        var baseDir = SetupDirectory();
        var bundled = Path.Combine(baseDir, "payload.zip");
        if (File.Exists(bundled)) return bundled;
        var resources = Path.Combine(baseDir, "..", "Resources");
        var inResources = Path.Combine(resources, "payload.zip");
        if (File.Exists(inResources)) return inResources;
        foreach (var root in new[] { baseDir, resources })
        {
            try
            {
                var zips = Directory.GetFiles(root, "AgentDock-*-macos-*.zip");
                if (zips.Length > 0) return zips.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
            catch (DirectoryNotFoundException)
            {
                // 候选目录不存在：继续下一个候选。
            }
        }
        var directory = Path.Combine(baseDir, "payload");
        return Directory.Exists(directory) ? directory : null;
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
            "未找到安装包。请把 payload.zip 放在安装程序旁边（或在 .app 的 Contents/Resources 内），或用 --payload 指定路径。");
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
}
