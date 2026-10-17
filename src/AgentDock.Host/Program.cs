using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentDock.Core;
using AgentDock.Engine;

namespace AgentDock.Host;

public static class Program
{
    public static string Version => typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Contains("--version")) { Console.WriteLine(Version); return 0; }
        var homeIndex = Array.IndexOf(args, "--home");
        var home = homeIndex >= 0 ? args.ElementAtOrDefault(homeIndex + 1) : null;
        if (home is null && args.Contains("--mcp") && File.Exists(Path.Combine(AppContext.BaseDirectory, "install.json")))
        {
            using var installed = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "install.json")));
            home = installed.RootElement.GetProperty("dataHome").GetString();
        }
        var client = new DockClient(home, args.Contains("--mcp") ? InstalledHost() : Environment.ProcessPath);
        try
        {
            if (args.Contains("--mcp")) { await new McpServer(client).RunAsync(Console.In, Console.Out); return 0; }
            if (!args.Contains("--host")) { Console.Error.WriteLine("AgentDock.Host --host | --mcp | --version"); return 2; }
            // A short exclusive launch lock makes simultaneous on-demand starts converge on one owner.
            Directory.CreateDirectory(client.Home);
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(client.Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            FileStream launch;
            try { launch = new(Path.Combine(client.Home, "launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return 0; }
            using (launch)
            {
                InstallState.RequireAvailable(client.Home);
                var vault = new CredentialVault(client.Home);
                await using var service = new TaskService(client.Home, new OpenCodeEngine(client.Home, () => vault.Read()));
                OpenCodeEngine.RecoverAbandonedJobs(client.Home);
                var packaged = PackageDefaults(service.Configuration);
                if (string.IsNullOrEmpty(service.Configuration.EnginePath) || File.Exists(packaged.EnginePath)) service.Configure(packaged);
                var application = new HostApplication(service, vault, client.Home);
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await File.WriteAllTextAsync(Path.Combine(client.Home, "connection.token"), token);
                if (OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(Path.Combine(client.Home, "connection.token"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await ServeAsync(client.PipeName, application, token);
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Never print exception messages from upstream responses or secret storage.
            Console.Error.WriteLine(ex is DockException dock ? dock.Code + ": " + dock.Message : "AgentDock 启动失败 (" + ex.GetType().Name + ")。");
            return 1;
        }
    }

    private static string InstalledHost()
    {
        var path = Environment.GetEnvironmentVariable("AGENTDOCK_HOST");
        if (!string.IsNullOrEmpty(path)) return path;
        // Installer-generated pointer belongs to AgentDock, never to the old MCP.
        var pointer = Path.Combine(InstallState.DefaultRoot, "install", "current.json");
        var pluginInstall = Path.Combine(AppContext.BaseDirectory, "install.json");
        if (File.Exists(pluginInstall))
        {
            using var plugin = JsonDocument.Parse(File.ReadAllText(pluginInstall));
            pointer = plugin.RootElement.GetProperty("hostManifest").GetString()!;
        }
        if (File.Exists(pointer))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pointer));
            if (doc.RootElement.TryGetProperty("host", out var host)) return host.GetString()!;
        }
        return Environment.ProcessPath!;
    }

    private static DockConfiguration PackageDefaults(DockConfiguration config)
    {
        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        return config with
        {
            EnginePath = Path.Combine(package, "engines", "opencode", OperatingSystem.IsWindows() ? "opencode.exe" : "opencode"),
            SandboxPath = Path.Combine(package, "engines", "codex", "bin", OperatingSystem.IsWindows() ? "codex.exe" : "codex"),
            ShellPath = OperatingSystem.IsWindows() ? Path.Combine(package, "engines", "git", "usr", "bin", "sh.exe") : "/bin/bash"
        };
    }

    private static async Task ServeAsync(string pipeName, HostApplication application, string token)
    {
        using var cancellation = new CancellationTokenSource();
        var maintenance = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            application.CleanupExpired();
            try { while (await timer.WaitForNextTickAsync(cancellation.Token)) application.CleanupExpired(); }
            catch (OperationCanceledException) { }
        });
        application.Exiting += () => cancellation.Cancel();
        try { await AcceptClientsAsync(pipeName, pipe => HandleAsync(pipe, application, token), cancellation.Token); }
        finally { cancellation.Cancel(); await maintenance; }
    }

    internal static async Task AcceptClientsAsync(string pipeName, Func<NamedPipeServerStream, Task> handle,
        CancellationToken cancellationToken)
    {
        var clients = new List<Task>();
        NamedPipeServerStream NewListener() => new(pipeName, PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var listener = NewListener();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(cancellationToken);
                var pipe = listener;
                // On Unix the last disposed instance removes the shared socket.
                // Retain the next listener before a fast request can finish.
                listener = NewListener();
                clients.RemoveAll(x => x.IsCompleted);
                // An async handler can execute synchronous lock/disk work before its first yield.
                // Dispatch it separately so that work cannot stop the pipe accept loop.
                clients.Add(Task.Run(() => handle(pipe)));
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Dispose(); await Task.WhenAll(clients); }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, HostApplication application, string token)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55)))
        {
            var shutdown = false;
            try
            {
                var line = await Wire.ReadLineAsync(reader, deadline.Token);
                if (line is null) return;
                ClientIdentity.RequireUnrestrictedClient(pipe);
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("token", out var supplied) || supplied.ValueKind != JsonValueKind.String ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(supplied.GetString()!)))
                    throw new DockException("Unauthorized", "无效的本地客户端身份。");
                var method = doc.RootElement.GetProperty("method").GetString()!;
                var parameters = doc.RootElement.TryGetProperty("parameters", out var p) ? p : default;
                var result = await application.InvokeAsync(method, parameters, deadline.Token);
                shutdown = method == "shutdown";
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { result }, JsonDefaults.Options));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var code = ex is DockException dock ? dock.Code : "InvalidOperation";
                var message = ex is DockException known ? known.Message : "请求无法完成，请检查参数或后台状态。";
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(new { error = new { code, message } }, JsonDefaults.Options)); }
                catch (IOException) { }
            }
            finally { if (shutdown) application.Exit(); }
        }
    }
}
