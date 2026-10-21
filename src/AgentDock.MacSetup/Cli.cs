using System.Reflection;
using System.Text;
using AgentDock.Setup;

namespace AgentDock.MacSetup;

internal enum MacSetupMode { Gui, Install, ApplyUpdate, Uninstall, Launch, Mcp, Status, Shutdown, LoginEnable, LoginDisable, Help, Version }

internal sealed class MacSetupOptions
{
    public MacSetupMode Mode { get; private set; } = MacSetupMode.Gui;
    public string? Payload { get; private set; }
    public string? Root { get; private set; }
    public string? OutFile { get; private set; }
    public bool Tray { get; private set; }
    public bool NoShellIntegration { get; private set; }
    public bool Silent { get; private set; }

    public static MacSetupOptions Parse(string[] args)
    {
        var options = new MacSetupOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--install": options.Mode = MacSetupMode.Install; break;
                case "--apply-update": options.Mode = MacSetupMode.ApplyUpdate; break;
                case "--uninstall": options.Mode = MacSetupMode.Uninstall; break;
                case "--launch": options.Mode = MacSetupMode.Launch; break;
                case "--mcp": options.Mode = MacSetupMode.Mcp; break;
                case "--status": options.Mode = MacSetupMode.Status; break;
                case "--shutdown": options.Mode = MacSetupMode.Shutdown; break;
                case "--login":
                    var action = Next(args, ref i);
                    options.Mode = action switch
                    {
                        "enable" => MacSetupMode.LoginEnable,
                        "disable" => MacSetupMode.LoginDisable,
                        _ => throw new ArgumentException($"--login 只接受 enable 或 disable：{action}")
                    };
                    break;
                case "--payload": options.Payload = Next(args, ref i); break;
                case "--root": options.Root = Next(args, ref i); break;
                case "--out": options.OutFile = Next(args, ref i); break;
                case "--silent": options.Silent = true; break;
                case "--tray": options.Tray = true; break;
                case "--no-shell-integration": options.NoShellIntegration = true; break;
                case "--help" or "-h": options.Mode = MacSetupMode.Help; break;
                case "--version": options.Mode = MacSetupMode.Version; break;
                default: throw new ArgumentException($"未知参数：{args[i]}");
            }
        }
        return options;
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"参数缺少值：{args[index]}");
        return args[++index];
    }
}

internal static class Cli
{
    public static int Run(string[] args)
    {
        MacSetupOptions options;
        try
        {
            options = MacSetupOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Error(ex.Message);
            return 2;
        }

        InstallLayout? layout = null;
        try
        {
            // 非 macOS 只允许在隔离 --root 上做载荷/门禁检查，禁止触碰真实安装根或系统集成。
            if (!OperatingSystem.IsMacOS() && options.Root is null &&
                options.Mode is not (MacSetupMode.Help or MacSetupMode.Version or MacSetupMode.Status))
            {
                throw new ArgumentException("非 macOS 环境只支持 --root 指向隔离目录做本地载荷检查（--status 除外）。");
            }

            // 稳定启动器未显式 --root 时：所属 <root>/AgentDock.app 的父目录带 install/current.json 即视为安装根；
            // 安装器（双击）场景没有该指针，仍使用默认根。
            var root = options.Root ?? MacPaths.InferRootFromBundle();
            layout = InstallLayout.Resolve(root);
            switch (options.Mode)
            {
                case MacSetupMode.Help:
                    PrintUsage();
                    return 0;
                case MacSetupMode.Version:
                    LogVersion();
                    return 0;
                case MacSetupMode.Status:
                    return Status(layout, options);
                case MacSetupMode.Install:
                    return Install(layout, options);
                case MacSetupMode.ApplyUpdate:
                    return ApplyUpdate(layout, options);
                case MacSetupMode.Uninstall:
                    return MacUninstaller.Run(layout, Log);
                case MacSetupMode.Launch:
                    MacLauncher.Launch(layout, options.Tray);
                    return 0;
                case MacSetupMode.Mcp:
                    return MacLauncher.Mcp(layout);
                case MacSetupMode.Shutdown:
                    return MacLauncher.Shutdown(layout, Log);
                case MacSetupMode.LoginEnable:
                    MacLoginIntegration.SetStartAtLogin(true, layout, Log);
                    return 0;
                case MacSetupMode.LoginDisable:
                    MacLoginIntegration.SetStartAtLogin(false, layout, Log);
                    return 0;
                default:
                    return Gui(layout, options);
            }
        }
        catch (Exception ex)
        {
            if (options.Mode is MacSetupMode.Install or MacSetupMode.ApplyUpdate or MacSetupMode.Gui)
                layout?.Log($"安装程序失败（{options.Mode}）：{ex.Message}");
            if (options.Mode == MacSetupMode.Gui && OperatingSystem.IsMacOS())
            {
                try
                {
                    MacDialog.Error(ex.Message);
                }
                catch
                {
                    // 对话框不可用时仍有 stderr 输出。
                }
            }
            Error("AgentDock 安装程序错误：" + ex.Message);
            return 1;
        }
    }

    /// <summary>双击 .app（无参数）：有 payload 时确认后安装并明确提示完成；否则读取 current.json 启动桌面程序。</summary>
    private static int Gui(InstallLayout layout, MacSetupOptions options)
    {
        var payload = MacInstaller.FindPayload();
        if (payload is null)
        {
            MacLauncher.Launch(layout, options.Tray);
            return 0;
        }

        var manifest = Payload.ReadManifest(payload);
        if (!MacDialog.ConfirmInstall(manifest.Version, layout.Root))
        {
            Log("已取消安装。");
            return 1;
        }
        var outcome = MacInstaller.Install(layout, payload, options.NoShellIntegration, Log);
        Log(outcome.AlreadyInstalled
            ? $"修复完成：版本 {outcome.Version}"
            : $"安装完成：版本 {outcome.Version} -> {outcome.VersionDir}");
        foreach (var warning in outcome.Warnings) Log("警告：" + warning);
        // 成功安装必须可见：显示完成对话框（可选择立即启动），绝不静默退出。
        if (MacDialog.InstallComplete(outcome.Version, layout.Root))
        {
            MacLauncher.Launch(layout, options.Tray);
        }
        return 0;
    }

    private static int Status(InstallLayout layout, MacSetupOptions options)
    {
        var current = layout.ReadCurrent();
        string? payload = null;
        string? payloadVersion = null;
        try
        {
            payload = MacInstaller.FindPayload();
            if (payload is not null)
            {
                try
                {
                    payloadVersion = Payload.ReadManifest(payload).Version;
                }
                catch
                {
                    payloadVersion = null;
                }
            }
        }
        catch
        {
            // 状态查询不应因探测失败而退出。
        }

        var view = new
        {
            installed = current is not null,
            platform = "osx-arm64",
            root = layout.Root,
            version = current?.Version,
            versionDir = current?.Root,
            host = current?.Host,
            desktop = current?.Desktop,
            engine = current?.Engine,
            plugin = current?.Plugin,
            dataHome = layout.DataDir,
            appBundle = layout.StableAppDir,
            launcher = layout.StableLauncher,
            appInstalled = File.Exists(layout.StableLauncher),
            updateLock = File.Exists(layout.UpdateLockPath),
            payload,
            payloadVersion
        };
        var json = SetupJson.Serialize(view);
        if (options.OutFile is { Length: > 0 })
        {
            File.WriteAllText(Path.GetFullPath(options.OutFile), json, new UTF8Encoding(false));
            Log($"状态已写入：{Path.GetFullPath(options.OutFile)}");
        }
        Log(json);
        return 0;
    }

    private static int Install(InstallLayout layout, MacSetupOptions options, string? payloadOverride = null)
    {
        var outcome = MacInstaller.Install(layout, payloadOverride ?? options.Payload, options.NoShellIntegration, Log);
        Log(outcome.AlreadyInstalled
            ? $"修复完成：版本 {outcome.Version}"
            : $"安装完成：版本 {outcome.Version} -> {outcome.VersionDir}");
        foreach (var warning in outcome.Warnings) Log("警告：" + warning);
        return 0;
    }

    private static int ApplyUpdate(InstallLayout layout, MacSetupOptions options)
    {
        if (options.Payload is null) throw new ArgumentException("--apply-update 需要 --payload <zip|目录>。");
        var payloadPath = MacInstaller.ResolvePayloadPath(options.Payload);
        var manifest = Payload.ReadManifest(payloadPath);
        var outcome = MacInstaller.ApplyUpdate(layout, payloadPath, manifest, options.NoShellIntegration, Log);
        Log($"更新完成：版本 {outcome.Version} -> {outcome.VersionDir}");
        foreach (var warning in outcome.Warnings) Log("警告：" + warning);
        return 0;
    }

    private static void LogVersion()
    {
        var assembly = typeof(Cli).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        Log($"AgentDock macOS 安装程序 {version}");
    }

    private static void PrintUsage()
    {
        Log("""
            AgentDock macOS 安装程序 / 稳定启动器

            用法：
              AgentDock.app                                  双击：安装包内确认安装；已安装启动器启动桌面
              AgentDock --install [--payload <zip|目录>] [--no-shell-integration] [--root <目录>]
              AgentDock --apply-update --payload <zip|目录> [--no-shell-integration]
              AgentDock --uninstall [--no-shell-integration] [--root <目录>]
              AgentDock --launch [--tray] [--root <目录>]
              AgentDock --mcp [--root <目录>]
              AgentDock --status [--root <目录>] [--out <json 文件>]
              AgentDock --shutdown [--root <目录>]
              AgentDock --login <enable|disable>
              AgentDock --version

            说明：
              稳定启动器位于 <root>/AgentDock.app（由安装器随包分发、原样复制，不含 payload）；
              未显式 --root 时从所属 .app 推导：<root>/AgentDock.app 的父目录含 install/current.json 即用该根。
              --payload       安装/更新载荷（payload.zip 或已展开目录）；默认在安装器 .app 的 Contents/Resources 内查找
              --root <目录>   覆盖安装根（默认 ~/Library/Application Support/AgentDock，供测试/隔离场景）
              --no-shell-integration  跳过 ~/Applications 链接与 LaunchServices 注册（自动化/测试）
              --shutdown      只请求空闲后台正常退出；存在未完成任务时后台拒绝（fail closed）
              --login         登录自启（默认关闭）：写入/删除 ~/Library/LaunchAgents 的 LaunchAgent
              --uninstall     卸载：删除本安装的版本目录、稳定插件源、稳定 .app、本安装的链接与登录自启配置；
                              默认保留 data/（任务数据）与 Keychain 凭据，绝不删除安装根或无关文件。
              安装/更新/修复/卸载共用同一门禁：更新标记 + 停止空闲后台 + 独占 launch.lock + 离线未完成任务复核；
              任一失败都不切换 current.json，旧版本与稳定插件保持可用；用户数据、Keychain 凭据和旧 MCP 配置不动。
            """);
    }

    private static void Log(string line)
    {
        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // 无控制台时忽略。
        }
    }

    private static void Error(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
            // 无控制台时忽略。
        }
    }
}
