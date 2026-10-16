using System.Reflection;
using Microsoft.Win32;

namespace AgentDock.Setup;

/// <summary>开始菜单快捷方式与“应用和功能”卸载入口。只写 HKCU，不需要管理员权限。</summary>
internal static class ShellIntegration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentDock";
    public const string GitHubUrl = "https://github.com/HoboCY/AgentDock";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AgentDock.lnk");

    public static void Register(InstallLayout layout, string version)
    {
        CreateShortcut(layout);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true)
            ?? throw new InvalidOperationException("无法写入卸载注册表项。");
        key.SetValue("DisplayName", "AgentDock");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "AgentDock");
        key.SetValue("URLInfoAbout", GitHubUrl);
        key.SetValue("InstallLocation", layout.Root);
        key.SetValue("DisplayIcon", layout.LauncherPath + ",0");
        key.SetValue("UninstallString", $"\"{layout.LauncherPath}\" --uninstall --root \"{layout.Root}\"");
        key.SetValue("QuietUninstallString", $"\"{layout.LauncherPath}\" --uninstall --silent --root \"{layout.Root}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            key.SetValue("EstimatedSize", EstimateSizeKb(layout), RegistryValueKind.DWord);
        }
        catch
        {
            // 体积统计属于锦上添花，失败不影响注册。
        }
    }

    public static void Unregister(InstallLayout layout)
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (run?.GetValue("AgentDock") is string command && command.StartsWith("\"" + layout.LauncherPath + "\"", StringComparison.OrdinalIgnoreCase))
            run.DeleteValue("AgentDock", false);
    }

    public static void RemoveShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }
        catch
        {
            // 快捷方式删除失败不阻塞卸载。
        }
    }

    private static void CreateShortcut(InstallLayout layout)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) throw new InvalidOperationException("当前系统缺少 WScript.Shell，无法创建快捷方式。");
        var shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("WScript.Shell 初始化失败。");
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath })
            ?? throw new InvalidOperationException("快捷方式创建失败。");
        var shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { layout.LauncherPath });
        shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { $"--launch --root \"{layout.Root}\"" });
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { layout.Root });
        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { layout.LauncherPath + ",0" });
        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "AgentDock 外部子代理控制台" });
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    private static int EstimateSizeKb(InstallLayout layout)
    {
        if (!Directory.Exists(layout.InstallDir)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(layout.InstallDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // 忽略瞬时文件。
            }
        }
        return (int)Math.Min(int.MaxValue, total / 1024);
    }
}
