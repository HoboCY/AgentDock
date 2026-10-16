namespace AgentDock.Setup;

internal static class Updater
{
    /// <summary>
    /// 用户明确选择后应用的更新流程，fail closed（由 OperationGate 统一实施）：
    ///   1. 已有安装才可更新，目标版本必须不同；
    ///   2. 先写入 install\update.lock（更新窗口内 Host 启动与 start/resume 都会被拒绝）；
    ///   3. 后台在线必须 CanUpdate 且无未完成任务，随后请求正常退出（shutdown confirm=false）；
    ///      后台离线则直接读取持久化任务，排队/运行/暂停任一存在即拒绝；状态损坏一律拒绝；
    ///   4. 确认后台已退出后独占 data\launch.lock 并持有到 current.json 切换完成，
    ///      防止更新期间被重新拉起或接纳新任务；
    ///   5. 任一步失败都保留旧版本可用；无论成败，门禁释放时都会移除本次更新标记。
    /// </summary>
    public static InstallOutcome ApplyUpdate(
        InstallLayout layout, string payloadPath, PayloadManifest manifest, bool noShellIntegration, Action<string> log)
    {
        Installer.ValidateForInstall(manifest);
        var current = layout.ReadCurrent() ?? throw new InvalidOperationException("尚未安装 AgentDock，无法应用更新。请先运行安装程序。");
        if (string.Equals(current.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"当前已是版本 {manifest.Version}，无需更新。");
        }

        var payloadSha256 = File.Exists(payloadPath) ? Payload.Sha256File(payloadPath) : null;
        using var gate = OperationGate.Enter(layout, manifest.Version, "update", log, payloadSha256);
        var (versionDir, reused) = Installer.StageAndPlace(layout, payloadPath, manifest, log);
        var warnings = Installer.FinalizeArtifacts(layout, versionDir, manifest, noShellIntegration, log);
        gate.Completed = true;
        layout.Log($"更新完成：{current.Version} -> {manifest.Version}（{(reused ? "复用已校验版本目录" : "新展开")}）");
        return new InstallOutcome(manifest.Version, versionDir, warnings, reused);
    }
}
