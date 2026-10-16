using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// 刷新 &lt;root&gt;/plugin 稳定插件源：跨版本路径不变，插件内不含任何用户密钥。
/// .mcp.json 的 command 指向稳定启动器绝对路径并显式携带 --root，启动器每次调用都动态读取
/// install/current.json，因此 Codex 插件缓存不会因 AgentDock 更新失效。
/// 旧插件源保留到 current.json 成功发布后才删除（失败时回滚）。
/// </summary>
internal static class MacPluginIntegration
{
    public static PendingSwap? Prepare(
        InstallLayout layout, CurrentInstall current, string launcherPath, List<string> warnings, Action<string> log)
    {
        var source = current.Plugin;
        if (!Directory.Exists(source))
        {
            warnings.Add("安装包未包含插件源（plugin/），Codex 接入暂不可用。");
            return null;
        }

        var staged = Path.Combine(layout.InstallDir, ".plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            MacFileSystem.CopyTree(source, staged);
            var pluginRoot = Path.Combine(staged, "plugins", "agentdock");
            var runtimeDir = Path.Combine(pluginRoot, "runtime");
            Directory.CreateDirectory(runtimeDir);

            // 与 Windows 插件同构的绝对路径清单；launcher 指向稳定启动器而非版本目录。
            var install = new PluginInstall(
                Schema: 1,
                Product: "AgentDock",
                Version: current.Version,
                InstallRoot: layout.Root,
                HostManifest: layout.CurrentManifestPath,
                Launcher: launcherPath,
                DataHome: layout.DataDir);
            layout.WriteJsonAtomic(Path.Combine(runtimeDir, "install.json"), install);

            WriteMcpJson(Path.Combine(pluginRoot, ".mcp.json"), launcherPath, layout.StablePluginDir, layout.Root);
            if (!File.Exists(launcherPath))
            {
                warnings.Add($"稳定启动器尚未就绪：{launcherPath}；请使用官方完整安装包重新安装。");
            }

            var swap = PendingSwap.Replace(staged, layout.StablePluginDir, layout.InstallDir);
            log($"已刷新插件源：{layout.StablePluginDir}");
            return swap;
        }
        catch
        {
            MacFileSystem.TryDelete(staged);
            throw;
        }
    }

    /// <summary>MCP stdio 代理通过稳定启动器调用；显式 --root 使启动器读取本安装的 current.json。</summary>
    public static void WriteMcpJson(string path, string launcherPath, string stablePluginDir, string root)
    {
        var document = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["agentdock"] = new Dictionary<string, object>
                {
                    ["command"] = launcherPath,
                    ["args"] = new[] { "--mcp", "--root", root },
                    ["cwd"] = Path.Combine(stablePluginDir, "plugins", "agentdock")
                }
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SetupJson.Serialize(document));
    }
}
