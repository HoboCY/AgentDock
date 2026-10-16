using System.Diagnostics;
using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// macOS 可选 shell 集成（--no-shell-integration 时完全跳过，绝不触碰 ~/Applications）：
///   1. 仅在 ~/Applications/AgentDock.app 缺失或已是指向本安装根 .app 的链接时，创建一个指向它的链接；
///      目标存在且与本安装无关时绝不替换（只记警告）。
///   2. 把 &lt;root&gt;/AgentDock.app 注册到当前用户 LaunchServices（Finder/Spotlight/open -a 可用）。
/// 只写当前用户数据库，不需要管理员权限；所有失败都降级为警告，不影响安装完成。
/// </summary>
internal static class MacShellIntegration
{
    private const string LsRegister =
        "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    public static void Register(InstallLayout layout, List<string> warnings, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            log("非 macOS：跳过 shell 集成。");
            return;
        }
        RegisterUserLink(layout, warnings, log);
        RegisterLaunchServices(layout, warnings, log);
    }

    private static void RegisterUserLink(InstallLayout layout, List<string> warnings, Action<string> log)
    {
        var link = MacPaths.UserApplicationLink;
        if (MacPaths.EntryExists(link))
        {
            if (MacPaths.IsLinkPointingTo(link, layout.StableAppDir))
            {
                log($"~/Applications 链接已指向本安装：{link}");
                return;
            }
            warnings.Add($"~/Applications/AgentDock.app 已存在且不属于本安装，未替换：{link}");
            return;
        }
        try
        {
            Directory.CreateDirectory(MacPaths.UserApplications);
            Directory.CreateSymbolicLink(link, layout.StableAppDir);
            log($"已创建 ~/Applications 链接：{link} -> {layout.StableAppDir}");
        }
        catch (Exception ex)
        {
            warnings.Add($"创建 ~/Applications 链接失败（不影响安装与启动）：{ex.Message}");
        }
    }

    private static void RegisterLaunchServices(InstallLayout layout, List<string> warnings, Action<string> log)
    {
        if (!File.Exists(LsRegister))
        {
            warnings.Add("未找到 lsregister，跳过 LaunchServices 注册（不影响安装与启动）。");
            return;
        }
        try
        {
            var start = new ProcessStartInfo(LsRegister)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(layout.StableAppDir);
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(15000) || process.ExitCode != 0)
            {
                warnings.Add("LaunchServices 注册未成功（不影响安装与启动）。");
                return;
            }
            log($"已注册稳定启动器：{layout.StableAppDir}");
        }
        catch (Exception ex)
        {
            warnings.Add($"LaunchServices 注册失败（不影响安装与启动）：{ex.Message}");
        }
    }
}
