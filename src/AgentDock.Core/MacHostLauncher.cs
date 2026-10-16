using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentDock.Core;

/// <summary>Starts the Host in the logged-in desktop's Keychain session, including calls arriving over SSH.</summary>
public static class MacHostLauncher
{
    public static string LabelFor(string home) => "org.agentdock.host." + DockClient.PipeFor(home);

    public static async Task StartAsync(string home, string executable, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(home);
        home = ProjectPaths.Canonicalize(home);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Clients may race before the Host has acquired its own launch lock.
        await using var coordination = await AcquireAsync(Path.Combine(home, "launch-agent.lock"), cancellationToken);
        InstallState.RequireAvailable(home);
        // An old Desktop or cached plugin must still launch the currently installed Host.
        executable = InstallState.CurrentHost(home) ?? executable;
        var client = new DockClient(home, executable);
        if (await IsReadyAsync(client, cancellationToken)) return;

        var domain = "gui/" + GetUserId();
        var service = domain + "/" + LabelFor(home);
        var existing = await LaunchctlAsync(["print", service], cancellationToken);
        if (existing.Code is not (0 or 113))
            throw new DockException("HostLaunchFailed", "无法确认后台启动注册状态，请检查 macOS 登录会话。");
        if (existing.Code == 0)
        {
            if (!Regex.IsMatch(existing.Output, @"(?m)^\s*state = not running\s*$"))
            {
                await WaitReadyAsync(client, cancellationToken);
                return;
            }
            // A stopped registration can still reference an older installation.
            var removed = await LaunchctlAsync(["bootout", service], cancellationToken);
            if (removed.Code != 0) throw new DockException("HostLaunchFailed", "无法移除已停止的后台注册，请重试。");
        }

        var environment = new XElement("dict", new XElement("key", "HOME"),
            new XElement("string", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        foreach (var name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                environment.Add(new XElement("key", name), new XElement("string", value));
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("plist",
            new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "Label"), new XElement("string", LabelFor(home)),
                new XElement("key", "ProgramArguments"), new XElement("array",
                    new XElement("string", executable), new XElement("string", "--host"),
                    new XElement("string", "--home"), new XElement("string", home)),
                new XElement("key", "EnvironmentVariables"), environment,
                new XElement("key", "RunAtLoad"), new XElement("true"),
                new XElement("key", "KeepAlive"), new XElement("false"),
                new XElement("key", "StandardOutPath"), new XElement("string", Path.Combine(home, "host.stdout.log")),
                new XElement("key", "StandardErrorPath"), new XElement("string", Path.Combine(home, "host.stderr.log")))));
        // Kept in the data directory, not Library/LaunchAgents: login startup remains opt-in.
        var plist = Path.Combine(home, "host-launch.plist");
        document.Save(plist);
        var launched = await LaunchctlAsync(["bootstrap", domain, plist], cancellationToken);
        if (launched.Code != 0)
            throw new DockException("HostLaunchFailed", "无法在 macOS 登录桌面会话启动后台。请先登录 Mac 桌面，再重试。");
        await WaitReadyAsync(client, cancellationToken);
    }

    public static async Task RemoveStoppedAsync(string home, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        await using var coordination = await AcquireAsync(Path.Combine(home, "launch-agent.lock"), cancellationToken);
        var service = "gui/" + GetUserId() + "/" + LabelFor(home);
        var state = await LaunchctlAsync(["print", service], cancellationToken);
        if (state.Code == 113) return;
        if (state.Code != 0) throw new DockException("HostLaunchFailed", "无法确认后台注册状态，拒绝移除。");
        if (!Regex.IsMatch(state.Output, @"(?m)^\s*state = not running\s*$"))
            throw new DockException("HostRunning", "后台尚未停止，不能移除其启动注册。");
        var removed = await LaunchctlAsync(["bootout", service], cancellationToken);
        if (removed.Code != 0) throw new DockException("HostLaunchFailed", "后台启动注册移除失败。");
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { await Task.Delay(100, cancellationToken); }
        }
    }

    private static async Task<bool> IsReadyAsync(DockClient client, CancellationToken cancellationToken)
    {
        try { await client.InvokeAsync<HostStatus>("status", cancellationToken: cancellationToken); return true; }
        catch (Exception error) when (error is IOException or TimeoutException) { return false; }
    }

    private static async Task WaitReadyAsync(DockClient client, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (await IsReadyAsync(client, cancellationToken)) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new DockException("HostUnavailable", "后台未能就绪，请检查安装或登录桌面会话；已有任务不会重新派发。");
    }

    private static async Task<(int Code, string Output)> LaunchctlAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("/bin/launchctl")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 launchctl。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            _ = await error;
            return (process.ExitCode, await output);
        }
        finally { if (!process.HasExited) process.Kill(); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getuid")]
    private static extern uint GetUserId();
}
