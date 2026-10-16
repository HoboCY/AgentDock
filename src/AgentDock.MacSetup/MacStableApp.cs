using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// 安装 &lt;root&gt;/AgentDock.app 稳定启动器：只把安装器 .app 内随包分发的独立启动器
/// （Contents/Resources/launcher/AgentDock.app，自带 MacSetup 可执行文件、不含 payload）原样复制过去。
/// 绝不复制安装器自身再裁剪 payload（改动签名内容不安全）；原目录保留到 current.json 成功发布后才删除。
/// </summary>
internal static class MacStableApp
{
    /// <summary>准备稳定启动器替换；返回 null 表示源缺失（开发输出目录）或无需替换。</summary>
    public static PendingSwap? Prepare(InstallLayout layout, List<string> warnings, Action<string> log)
    {
        var bundle = MacPaths.FindContainingBundle();
        if (bundle is null)
        {
            // 开发输出目录（非 .app）没有随包启动器：警告跳过，隔离测试仍可验证其余流程。
            warnings.Add("未从安装器 .app 内运行，未安装稳定启动器；插件接入需要它，请使用官方完整安装包。");
            return null;
        }
        if (InstallLayout.SamePath(bundle, layout.StableAppDir))
        {
            // Installed launcher can repair/apply a payload without containing another copy of itself.
            return null;
        }
        var source = Path.Combine(bundle, "Contents", "Resources", "launcher", "AgentDock.app");
        if (!Directory.Exists(source))
        {
            throw new InvalidDataException($"安装器 .app 内缺少独立稳定启动器（fail closed）：{source}");
        }
        var executable = Path.Combine(source, "Contents", "MacOS", "AgentDock");
        if (!File.Exists(executable))
        {
            throw new InvalidDataException($"稳定启动器缺少可执行文件（fail closed）：{executable}");
        }

        var destination = layout.StableAppDir;
        if (InstallLayout.SamePath(source, destination))
        {
            log("当前进程已在稳定启动器内运行，无需复制。");
            return null;
        }

        var staging = Path.Combine(layout.InstallDir, ".app-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 原样复制：不排除、不改写任何文件，保证稳定 .app 与随包分发内容一致。
            MacFileSystem.CopyTree(source, staging);
            var swap = PendingSwap.Replace(staging, destination, layout.InstallDir);
            log($"稳定启动器已就位：{destination}");
            return swap;
        }
        catch
        {
            MacFileSystem.TryDelete(staging);
            throw;
        }
    }
}
