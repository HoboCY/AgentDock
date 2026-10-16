using AgentDock.Core;
using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// macOS 登录/启动注册集成：
///   1. 安装/更新/卸载门禁内清理 MacHostLauncher 创建的 launchd 后台注册（指向旧版本路径的注册必须先移除）；
///   2. 可选登录自启（默认关闭）：显式 --login enable 才通过 Core 的 MacLoginStartup 写入
///      ~/Library/LaunchAgents 的 LaunchAgent（与 Host 配置路径使用同一实现，不再各自维护固定标签）。
/// </summary>
internal static class MacLoginIntegration
{
    private static readonly TimeSpan RegistrationGrace = TimeSpan.FromSeconds(10);

    /// <summary>门禁内调用：只在后台已停止（launch.lock 由门禁持有）时移除 launchd 注册，绝不终止任务。</summary>
    public static void RemoveStoppedHostRegistration(InstallLayout layout, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var deadline = DateTime.UtcNow + RegistrationGrace;
        while (true)
        {
            try
            {
                MacHostLauncher.RemoveStoppedAsync(layout.DataDir).GetAwaiter().GetResult();
                log("已移除后台的 launchd 启动注册；下次按需启动会使用当前版本。");
                return;
            }
            catch (DockException ex) when (ex.Code == "HostRunning" && DateTime.UtcNow < deadline)
            {
                // 后台停机与 launchctl 状态更新之间存在短暂窗口；门禁已持有 launch.lock，稍后重试即可。
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>与 Core 共用同一 LaunchAgent 定义（按 home 派生标签/路径），默认关闭，只由显式命令触发。</summary>
    public static void SetStartAtLogin(bool enable, InstallLayout layout, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("登录自启仅在 macOS 上实现；Windows 由安装程序与 Host 管理。");
        }
        var client = new DockClient(layout.DataDir);
        client.EnsureHostAsync().GetAwaiter().GetResult();
        var view = client.InvokeAsync<ConfigurationView>("config.get").GetAwaiter().GetResult();
        client.InvokeAsync<ConfigurationView>("config.set", new
        {
            configuration = view.Configuration with { StartAtLogin = enable }
        }).GetAwaiter().GetResult();
        log(enable
            ? $"已启用登录自启（下次登录生效）：{MacLoginStartup.RegistrationPath(layout.DataDir)}"
            : "已关闭登录自启。");
    }
}
