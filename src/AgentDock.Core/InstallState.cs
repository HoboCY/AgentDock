using System.Text.Json;

namespace AgentDock.Core;

public static class InstallState
{
    public static string DefaultRoot => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "AgentDock")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock");
    public static string DefaultHome => Path.Combine(DefaultRoot, "data");

    public static string? CurrentHost(string home)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(home))!;
        var pointer = Path.Combine(root, "install", "current.json");
        if (!File.Exists(pointer)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pointer));
            var current = document.RootElement;
            var host = current.GetProperty("host").GetString()!;
            if (current.GetProperty("schema").GetInt32() != 1 || !Path.IsPathFullyQualified(host) ||
                ProjectPaths.Canonicalize(current.GetProperty("dataHome").GetString()!) != ProjectPaths.Canonicalize(home) ||
                !ProjectPaths.Within(ProjectPaths.Canonicalize(Path.GetDirectoryName(host)!), ProjectPaths.Canonicalize(Path.Combine(root, "install"))) ||
                !File.Exists(host)) throw new InvalidDataException();
            return host;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new DockException("InstallInvalid", "当前安装指针不可用，请修复 AgentDock 安装。");
        }
    }
    public static string UpdateMarker(string home) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(home))!, "install", "update.lock");
    public static void RequireAvailable(string home)
    {
        if (File.Exists(UpdateMarker(home)))
            throw new DockException("UpdateInProgress", "AgentDock 正在更新，暂不启动后台或接收新任务。原任务身份会保留。");
    }
}
