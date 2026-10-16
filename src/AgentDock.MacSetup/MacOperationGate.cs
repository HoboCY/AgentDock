using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// Windows 共用 OperationGate 的 macOS 封装：门禁本身（更新标记、停止空闲后台、独占 launch.lock、
/// 离线未完成任务复核）完全复用共享源码；进入门禁后再清理 launchd 里指向旧安装路径的后台注册，
/// 让下一次按需启动重新读取当前版本。清理失败时释放门禁并拒绝继续（fail closed）。
/// </summary>
internal static class MacOperationGate
{
    public static OperationGate Enter(
        InstallLayout layout, string targetVersion, string operation, Action<string> log, string? payloadSha256 = null)
    {
        var gate = OperationGate.Enter(layout, targetVersion, operation, log, payloadSha256);
        try
        {
            MacLoginIntegration.RemoveStoppedHostRegistration(layout, log);
            return gate;
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }
}
