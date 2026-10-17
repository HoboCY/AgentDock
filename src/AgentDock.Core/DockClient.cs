using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentDock.Core;

public sealed class DockClient(string? home = null, string? hostExecutable = null)
{
    public string Home { get; } = Path.GetFullPath(home ?? Environment.GetEnvironmentVariable("AGENTDOCK_HOME") ??
        InstallState.DefaultHome);

    public string PipeName => PipeFor(Home);
    public static string PipeFor(string home) => "agentdock-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Path.GetFullPath(home).ToUpperInvariant() : ProjectPaths.Canonicalize(home))))[..24];

    public async Task EnsureHostAsync(CancellationToken cancellationToken = default)
    {
        try { await InvokeCoreAsync<HostStatus>("status", null, 700, cancellationToken); return; }
        catch (Exception ex) when (ex is TimeoutException or IOException) { }
        InstallState.RequireAvailable(Home);
        var executable = (OperatingSystem.IsMacOS() ? InstallState.CurrentHost(Home) : null) ??
            hostExecutable ?? Environment.GetEnvironmentVariable("AGENTDOCK_HOST") ??
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "host", OperatingSystem.IsWindows() ? "AgentDock.Host.exe" : "AgentDock.Host"));
        if (!File.Exists(executable)) throw new DockException("HostMissing", "未找到配套后台程序。请重新安装完整 AgentDock 包。");
        if (OperatingSystem.IsMacOS())
        {
            await MacHostLauncher.StartAsync(Home, executable, cancellationToken);
            return;
        }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--host"); start.ArgumentList.Add("--home"); start.ArgumentList.Add(Home);
        using var process = Process.Start(start) ?? throw new IOException("后台启动失败。");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            try { await InvokeCoreAsync<HostStatus>("status", null, 700, cancellationToken); return; }
            catch (Exception ex) when (ex is TimeoutException or IOException) { }
            await Task.Delay(200, cancellationToken);
        }
        throw new DockException("HostUnavailable", "后台未能启动；请检查安装和数据目录。已有任务不会自动重新派发。");
    }

    public Task<T> InvokeAsync<T>(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        InvokeCoreAsync<T>(method, parameters, 3000, cancellationToken);

    private async Task<T> InvokeCoreAsync<T>(string method, object? parameters, int connectTimeoutMs, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // Allow a briefly busy listener to recover before sending anything. Never replay a written request.
        // Host discovery keeps its short probe above so a stopped Host still starts promptly.
        await pipe.ConnectAsync(connectTimeoutMs, cancellationToken);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var token = await File.ReadAllTextAsync(Path.Combine(Home, "connection.token"), cancellationToken);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { method, parameters, token }, JsonDefaults.Options).AsMemory(), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        var line = await Wire.ReadLineAsync(reader, deadline.Token) ?? throw new IOException("后台连接已断开。");
        using var response = JsonDocument.Parse(line);
        if (response.RootElement.TryGetProperty("error", out var error))
            throw new DockException(error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
        return response.RootElement.GetProperty("result").Deserialize<T>(JsonDefaults.Options)
            ?? throw new IOException("后台返回空响应。");
    }
}

public static class Wire
{
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        var buffer = new char[1];
        var text = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) != 0)
        {
            if (buffer[0] == '\n') return text.ToString();
            if (text.Length >= 4 * 1024 * 1024) throw new DockException("MessageTooLarge", "消息超过 4 MiB 限制。");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : text.ToString();
    }
}
