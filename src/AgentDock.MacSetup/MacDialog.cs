using System.Diagnostics;

namespace AgentDock.MacSetup;

/// <summary>
/// 双击 .app（无参数）时的最简原生对话框：通过 /usr/bin/osascript 显示安装确认/错误。
/// 参数以 ArgumentList 传递、AppleScript 字符串显式转义，不经过 shell；非交互场景（终端 CLI）不调用。
/// </summary>
internal static class MacDialog
{
    public static bool ConfirmInstall(string version, string root)
    {
        var message = $"安装 AgentDock {version}？\n\n安装位置：{root}\n用户数据与 Keychain 凭据不会被删除。";
        var result = Run($"display dialog \"{Escape(message)}\" with title \"AgentDock\" " +
            "buttons {\"取消\", \"安装\"} default button \"安装\" with icon note");
        return result.ExitCode == 0 && result.Output.Contains("安装", StringComparison.Ordinal);
    }

    public static void Error(string message)
    {
        _ = Run($"display dialog \"{Escape("安装未完成：" + message)}\" with title \"AgentDock\" " +
            "buttons {\"好\"} default button \"好\" with icon stop");
    }

    /// <summary>安装成功后的完成确认；返回 true 表示用户选择立即启动。</summary>
    public static bool InstallComplete(string version, string root)
    {
        var message = $"AgentDock {version} 安装完成。\n\n启动器：{Path.Combine(root, "AgentDock.app")}\n" +
            "用户数据与 Keychain 凭据未被改动。\n\n现在启动吗？";
        var result = Run($"display dialog \"{Escape(message)}\" with title \"AgentDock\" " +
            "buttons {\"稍后\", \"启动\"} default button \"启动\" with icon note");
        return result.ExitCode == 0 && result.Output.Contains("启动", StringComparison.Ordinal);
    }

    public static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n");

    private static (int ExitCode, string Output) Run(string script)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("图形对话框仅在 macOS 上可用。");
        var start = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 osascript。");
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
