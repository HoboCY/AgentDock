using System.Diagnostics;
using AgentDock.Core;
using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// 稳定启动器的运行模式：--launch 读取 current.json 启动桌面程序；--mcp 读取 current.json 代理当前后台；
/// --shutdown（测试/自动化）请求空闲后台正常退出。两种模式都动态读取指针，绝不固定到某个版本目录。
/// 后台按需启动只走 DockClient.EnsureHostAsync → MacHostLauncher（登录桌面 Keychain 会话），不直接 SSH/启动后台。
/// </summary>
internal static class MacLauncher
{
    public static void Launch(InstallLayout layout, bool tray)
    {
        var current = layout.ReadCurrent() ?? throw new InvalidOperationException("尚未安装 AgentDock。请先运行安装程序。");
        var desktop = current.Desktop;
        if (!File.Exists(desktop))
        {
            throw new InvalidOperationException($"桌面程序缺失：{desktop}。请使用完整安装包重新安装。");
        }
        var start = new ProcessStartInfo(desktop)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(desktop)!
        };
        start.ArgumentList.Add("--home");
        start.ArgumentList.Add(current.DataHome);
        if (tray) start.ArgumentList.Add("--tray");
        ConfigureChildEnvironment(start, current.Root, current.Host);
        Process.Start(start)?.Dispose();
    }

    /// <summary>stdio MCP 代理：把 Codex 的 stdio 原样转给当前版本 Host 的 --mcp；不改变后台生命周期。</summary>
    public static int Mcp(InstallLayout layout)
    {
        var current = layout.ReadCurrent() ?? throw new InvalidOperationException("尚未安装 AgentDock。请先运行安装程序。");
        var host = current.Host;
        if (!File.Exists(host))
        {
            throw new InvalidOperationException($"后台程序缺失：{host}。请使用完整安装包重新安装。");
        }
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--mcp");
        start.ArgumentList.Add("--home");
        start.ArgumentList.Add(current.DataHome);
        ConfigureChildEnvironment(start, current.Root, host);
        using var child = Process.Start(start) ?? throw new IOException("后台代理启动失败。");
        var stdin = PumpAsync(Console.OpenStandardInput(), child.StandardInput.BaseStream);
        var stdout = PumpAsync(child.StandardOutput.BaseStream, Console.OpenStandardOutput());
        var stderr = PumpAsync(child.StandardError.BaseStream, Console.OpenStandardError());
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (!child.HasExited) child.Kill();
            }
            catch
            {
                // 代理进程清理失败不影响后台任务。
            }
        };
        child.WaitForExit();
        // stdin 泵随子进程退出而结束（写端已关闭）；stdout/stderr 必须排空后再返回。
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        _ = stdin;
        return child.ExitCode;
    }

    /// <summary>请求空闲后台正常退出；存在未完成任务时由后台拒绝（confirm=false，fail closed）。</summary>
    public static int Shutdown(InstallLayout layout, Action<string> log)
    {
        var probe = HostControl.Probe(layout);
        if (!probe.Alive)
        {
            log("后台未运行。");
            return 0;
        }
        if (probe.Error is not null)
        {
            throw new InvalidOperationException($"后台状态异常（{probe.Error}），为安全起见拒绝发送停止请求。");
        }
        log($"正在请求后台正常退出（PID {probe.Status!.ProcessId}）...");
        HostControl.Shutdown(layout);
        log("后台已退出。");
        return 0;
    }

    /// <summary>
    /// 把随包 Git 的 bin 目录加入子进程 PATH（Host → launchd plist → 引擎子进程继承），
    /// 并固定 AGENTDOCK_HOST 为当前版本后台，避免解析到旧路径。不修改用户环境。
    /// </summary>
    private static void ConfigureChildEnvironment(ProcessStartInfo start, string versionDir, string hostPath)
    {
        start.Environment["AGENTDOCK_HOST"] = hostPath;
        var gitBin = Path.Combine(versionDir, "engines", "git", "bin");
        if (!Directory.Exists(gitBin)) return;
        var path = start.Environment.TryGetValue("PATH", out var existing) && existing is { Length: > 0 }
            ? existing
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        start.Environment["PATH"] = gitBin + Path.PathSeparator + path;
    }

    private static async Task PumpAsync(Stream from, Stream to)
    {
        try
        {
            await from.CopyToAsync(to);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 对端关闭属于正常结束。
        }
        try
        {
            await to.FlushAsync();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 忽略关闭竞态。
        }
        try
        {
            to.Close();
        }
        catch
        {
            // 忽略关闭失败。
        }
    }
}
