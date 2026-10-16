namespace AgentDock.Core;

public static class InstallState
{
    public static string UpdateMarker(string home) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(home))!, "install", "update.lock");
    public static void RequireAvailable(string home)
    {
        if (File.Exists(UpdateMarker(home)))
            throw new DockException("UpdateInProgress", "AgentDock 正在更新，暂不启动后台或接收新任务。原任务身份会保留。");
    }
}
