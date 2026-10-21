using System.Globalization;
using System.Reflection;

namespace AgentDock.Setup;

internal enum SetupMode { Gui, Install, Launch, Uninstall, ApplyUpdate, Status, CleanupSelf, Help, Version }

internal sealed class SetupOptions
{
    public SetupMode Mode { get; private set; } = SetupMode.Gui;
    public string? Payload { get; private set; }
    public string? Root { get; private set; }
    public string? OutFile { get; private set; }
    public bool Silent { get; private set; }
    public bool Tray { get; private set; }
    public bool NoShellIntegration { get; private set; }
    public string? CleanupTarget { get; private set; }
    public int WaitPid { get; private set; }

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--install": options.Mode = SetupMode.Install; break;
                case "--launch": options.Mode = SetupMode.Launch; break;
                case "--uninstall": options.Mode = SetupMode.Uninstall; break;
                case "--apply-update": options.Mode = SetupMode.ApplyUpdate; break;
                case "--status": options.Mode = SetupMode.Status; break;
                case "--cleanup-self": options.Mode = SetupMode.CleanupSelf; options.CleanupTarget = Next(args, ref i); break;
                case "--wait-pid":
                    options.WaitPid = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--payload": options.Payload = Next(args, ref i); break;
                case "--root": options.Root = Next(args, ref i); break;
                case "--out": options.OutFile = Next(args, ref i); break;
                case "--silent": options.Silent = true; break;
                case "--tray": options.Tray = true; break;
                case "--no-shell-integration": options.NoShellIntegration = true; break;
                case "--help" or "-h": options.Mode = SetupMode.Help; break;
                case "--version": options.Mode = SetupMode.Version; break;
                default: throw new ArgumentException($"未知参数：{args[i]}（使用 --help 查看用法）");
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
        SetupOptions options;
        try
        {
            options = SetupOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Error(ex.Message);
            return 2;
        }

        var layout = InstallLayout.Resolve(options.Root);
        try
        {
            switch (options.Mode)
            {
                case SetupMode.Help:
                    PrintUsage();
                    return 0;
                case SetupMode.Version:
                    var assembly = typeof(Cli).Assembly;
                    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
                    Log($"AgentDock Setup {version}");
                    return 0;
                case SetupMode.Status:
                    return Status(layout, options);
                case SetupMode.Install:
                    return Install(layout, options);
                case SetupMode.Launch:
                    Installer.Launch(layout, options.Tray);
                    return 0;
                case SetupMode.Uninstall:
                    return Uninstall(layout, options);
                case SetupMode.ApplyUpdate:
                    return ApplyUpdate(layout, options);
                case SetupMode.CleanupSelf:
                    if (options.CleanupTarget is null) throw new ArgumentException("--cleanup-self 需要目标路径。");
                    Uninstaller.CleanupSelf(options.CleanupTarget, options.WaitPid);
                    return 0;
                default:
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm(layout));
                    return 0;
            }
        }
        catch (Exception ex) when (options.Mode is SetupMode.Install or SetupMode.ApplyUpdate)
        {
            layout.Log($"安装程序失败（{options.Mode}）：{ex.Message}");
            throw;
        }
    }

    private static int Status(InstallLayout layout, SetupOptions options)
    {
        var current = layout.ReadCurrent();
        string? payload = null;
        string? payloadVersion = null;
        try
        {
            payload = Installer.FindPayload();
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
            root = layout.Root,
            version = current?.Version,
            versionDir = current?.Root,
            host = current?.Host,
            desktop = current?.Desktop,
            engine = current?.Engine,
            plugin = current?.Plugin,
            dataHome = layout.DataDir,
            launcher = layout.LauncherPath,
            updateLock = File.Exists(layout.UpdateLockPath),
            bundledPayload = Payload.HasBundledPayload,
            payload,
            payloadVersion
        };
        var json = SetupJson.Serialize(view);
        if (options.OutFile is { Length: > 0 })
        {
            File.WriteAllText(Path.GetFullPath(options.OutFile), json, new System.Text.UTF8Encoding(false));
            Log($"状态已写入：{Path.GetFullPath(options.OutFile)}");
        }
        Log(json);
        return 0;
    }

    private static int Install(InstallLayout layout, SetupOptions options)
    {
        var outcome = Installer.Install(layout, options.Payload, options.NoShellIntegration, Log);
        Log(outcome.AlreadyInstalled
            ? $"修复完成：版本 {outcome.Version}"
            : $"安装完成：版本 {outcome.Version} -> {outcome.VersionDir}");
        foreach (var warning in outcome.Warnings) Log("警告：" + warning);
        return 0;
    }

    private static int Uninstall(InstallLayout layout, SetupOptions options)
    {
        if (!options.Silent)
        {
            var confirm = MessageBox.Show(
                $"确定卸载 AgentDock 吗？\n\n程序文件将被删除；用户数据保留在：\n{layout.DataDir}",
                "AgentDock 卸载",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                Log("已取消卸载。");
                return 1;
            }
        }
        Uninstaller.Uninstall(layout, options.NoShellIntegration, Log);
        Log("卸载完成。用户数据未删除。");
        return 0;
    }

    private static int ApplyUpdate(InstallLayout layout, SetupOptions options)
    {
        var payloadPath = Installer.ResolvePayloadPath(options.Payload);
        var manifest = Payload.ReadManifest(payloadPath);
        var outcome = Updater.ApplyUpdate(layout, payloadPath, manifest, options.NoShellIntegration, Log);
        Log($"更新完成：版本 {outcome.Version} -> {outcome.VersionDir}");
        foreach (var warning in outcome.Warnings) Log("警告：" + warning);
        return 0;
    }

    private static void PrintUsage()
    {
        Log("""
            AgentDock 安装程序

            用法：
              AgentDockSetup.exe                              图形界面
              AgentDockSetup.exe --install [--payload <zip|目录>] [--silent] [--no-shell-integration] [--root <目录>]
              AgentDockSetup.exe --launch [--tray] [--root <目录>]
              AgentDockSetup.exe --uninstall [--silent]
              AgentDockSetup.exe --apply-update [--payload <zip|目录>] [--silent]
              AgentDockSetup.exe --status [--root <目录>] [--out <json 文件>]
              AgentDockSetup.exe --version

            说明：
              --root <目录>   覆盖安装根目录（默认 %LOCALAPPDATA%\AgentDock，供测试/便携场景使用）
              --out <文件>    把 --status 的 JSON 写入文件（GUI 子系统程序不便直接管道捕获）
              --silent        不显示确认对话框（卸载）
              安装与更新默认使用内置程序包；--payload 仅供维护或旧版分体包使用。
              更新只由用户主动选择；有排队/运行/暂停任务时拒绝应用更新。
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
            // GUI 模式下没有控制台输出。
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
            // 忽略无控制台场景。
        }
    }
}
