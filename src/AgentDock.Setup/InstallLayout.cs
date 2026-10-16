namespace AgentDock.Setup;

/// <summary>
/// 每用户安装布局：
///   &lt;root&gt;/AgentDock.exe                      稳定启动器（Windows：安装程序自身副本）
///   &lt;root&gt;/AgentDock.app                      稳定启动器（macOS：随安装器分发的独立 .app 原样副本）
///   &lt;root&gt;/install/current.json               当前版本指针（跨版本稳定）
///   &lt;root&gt;/install/&lt;version&gt;/...              固定版本组合的程序文件
///   &lt;root&gt;/install/machine.json                只读检测到的本机信息
///   &lt;root&gt;/install/update.lock                 更新中标记（fail closed）
///   &lt;root&gt;/plugin/                             稳定插件源（Codex 本地 marketplace 指向这里）
///   &lt;root&gt;/data/                               用户数据（安装/卸载都不清理）
///   &lt;root&gt;/data/launch.lock                    Host 生命周期锁（安装/更新/卸载共用门禁）
/// </summary>
internal sealed class InstallLayout(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string InstallDir => Path.Combine(Root, "install");
    public string CurrentManifestPath => Path.Combine(InstallDir, "current.json");
    public string MachinePath => Path.Combine(InstallDir, "machine.json");
    public string UpdateLockPath => Path.Combine(InstallDir, "update.lock");
    public string SetupLogPath => Path.Combine(InstallDir, "setup.log");
    public string DataDir => Path.Combine(Root, "data");
    public string LauncherPath => Path.Combine(Root, "AgentDock.exe");
    public string StablePluginDir => Path.Combine(Root, "plugin");
    /// <summary>macOS 稳定启动器 .app（位于安装根，跨版本有效；Windows 安装不使用）。</summary>
    public string StableAppDir => Path.Combine(Root, "AgentDock.app");
    /// <summary>macOS 稳定启动器可执行文件（.app 内的 MacSetup，不携带 payload）。</summary>
    public string StableLauncher => Path.Combine(StableAppDir, "Contents", "MacOS", "AgentDock");
    /// <summary>Host 生命周期锁：Host 启动即独占持有；安装/更新/卸载在切换文件时必须独占持有。</summary>
    public string LaunchLockPath => Path.Combine(DataDir, "launch.lock");

    public static InstallLayout Resolve(string? rootOverride) => new(
        rootOverride is { Length: > 0 } ? rootOverride : DefaultRoot);

    /// <summary>
    /// 每用户默认安装根：Windows 为 %LOCALAPPDATA%\AgentDock；
    /// macOS 为 ~/Library/Application Support/AgentDock（用户名从用户主目录推导，不硬编码）。
    /// </summary>
    public static string DefaultRoot => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "AgentDock")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock");

    public string VersionDir(string version) => Path.Combine(InstallDir, SanitizeVersion(version));

    public static string SanitizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("版本号为空。");
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            if (version.Contains(invalid)) throw new InvalidDataException($"版本号包含非法字符：{version}");
        }
        if (version.Contains("..")) throw new InvalidDataException($"版本号非法：{version}");
        return version;
    }

    public CurrentInstall? ReadCurrent()
    {
        if (!File.Exists(CurrentManifestPath)) return null;
        try
        {
            return SetupJson.Deserialize<CurrentInstall>(File.ReadAllText(CurrentManifestPath));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"当前安装指针损坏：{CurrentManifestPath}（{ex.Message}）。请重新安装。", ex);
        }
    }

    public void WriteCurrent(CurrentInstall current) => WriteJsonAtomic(CurrentManifestPath, current);

    public void WriteMachine(MachineInfo machine) => WriteJsonAtomic(MachinePath, machine);

    public void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, SetupJson.Serialize(value));
        File.Move(tmp, path, overwrite: true);
    }

    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            File.AppendAllText(SetupLogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响安装流程。
        }
    }

    public static bool SamePath(string a, string b) => OperatingSystem.IsMacOS()
        ? string.Equals(AgentDock.Core.ProjectPaths.Canonicalize(a), AgentDock.Core.ProjectPaths.Canonicalize(b), StringComparison.Ordinal)
        : string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
