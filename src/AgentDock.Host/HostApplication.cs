using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Core;
using AgentDock.Engine;
using Microsoft.Win32;

namespace AgentDock.Host;

public sealed class HostApplication(TaskService service, CredentialVault vault, string home)
{
    public event Action? Exiting;
    public void Exit() => Exiting?.Invoke();
    public void CleanupExpired()
    {
        service.Cleanup(true);
        CleanupArtifacts();
    }
    private void CleanupArtifacts() => OpenCodeEngine.CleanupArtifacts(home,
        service.List().Where(t => t.BodyAvailable).Select(t => t.Id));
    private HostStatus Status()
    {
        var count = service.List().Count(t => t.Status.IsUnfinished());
        return new(Program.Version, Environment.ProcessId, count, count == 0, OpenCodeEngine.ExecutionProfile);
    }
    private ConfigurationView Configuration() => new(service.Configuration, vault.IsConfigured, CoexistenceWarnings());

    public async Task<object> InvokeAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "status": return Status();
            case "start":
                InstallState.RequireAvailable(home);
                if (!vault.IsConfigured) throw new DockException("CredentialRequired", "请先在 AgentDock 主面板独立配置 OpenCode Go 凭据。");
                return service.Start(args.Deserialize<TaskRequest>(JsonDefaults.Options) ?? throw new DockException("InvalidRequest", "任务参数为空。"));
            case "list": return service.List(Optional(args, "project"));
            case "get": return service.Get(Required(args, "id"), Number(args, "after"));
            case "wait": return await service.WaitAsync(Required(args, "id"), Number(args, "after"),
                TimeSpan.FromSeconds(args.TryGetProperty("waitSeconds", out var wait) ? wait.GetDouble() : 30), cancellationToken);
            case "stop": return service.Stop(Required(args, "id"));
            case "resume": InstallState.RequireAvailable(home); return service.Resume(Required(args, "id"));
            case "config.get": return Configuration();
            case "config.set":
                var next = args.GetProperty("configuration").Deserialize<DockConfiguration>(JsonDefaults.Options)
                    ?? throw new DockException("InvalidConfiguration", "配置为空。");
                TaskService.ValidateConfiguration(next);
                var previous = service.Configuration;
                if (next.StartAtLogin != previous.StartAtLogin) SetStartAtLogin(next.StartAtLogin);
                try { service.Configure(next); }
                catch { if (next.StartAtLogin != previous.StartAtLogin) SetStartAtLogin(previous.StartAtLogin); throw; }
                return Configuration();
            case "credential.set": vault.Set(Required(args, "key")); return Configuration();
            case "cleanup.preview": return service.Cleanup(false, Optional(args, "scope") ?? "expired", Optional(args, "project"));
            case "cleanup.apply":
                var cleaned = service.Cleanup(true, Optional(args, "scope") ?? "expired", Optional(args, "project"), Required(args, "revision"));
                CleanupArtifacts();
                return cleaned;
            case "shutdown":
                await service.ShutdownAsync(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("confirm", out var confirm) && confirm.GetBoolean());
                return Status();
            default: throw new DockException("UnknownMethod", "未知后台操作。");
        }
    }

    private static string Required(JsonElement value, string name) => Optional(value, name) ?? throw new DockException("MissingArgument", "缺少参数：" + name);
    private static string? Optional(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;
    private static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetInt64() : 0;

    private static string[] CoexistenceWarnings()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        try
        {
            if (!File.Exists(config)) return [];
            var text = File.ReadAllText(config);
            var match = Regex.Match(text, @"(?ms)^\[mcp_servers\.[""']?deepseek_agents[""']?\]\s*(?<body>.*?)(?=^\[|\z)");
            return match.Success && !Regex.IsMatch(match.Groups["body"].Value, @"(?m)^enabled\s*=\s*false\s*$")
                ? ["发现旧 deepseek_agents 配置，可能重复派发。请自行确认停用旧入口；AgentDock 不修改旧配置。"] : [];
        }
        catch (IOException) { return ["无法检查旧 MCP 是否同时启用，请自行确认，避免重复派发。"]; }
        catch (UnauthorizedAccessException) { return ["旧 MCP 配置不可读，请自行确认是否已停用。"]; }
    }

    private void SetStartAtLogin(bool enabled)
    {
        if (OperatingSystem.IsMacOS()) { MacLoginStartup.Configure(home, enabled); return; }
        if (!OperatingSystem.IsWindows()) throw new DockException("PlatformNotValidated", "此平台登录自启尚未完成实机验证。");
        var root = Directory.GetParent(Path.GetFullPath(home))!.FullName;
        var launcher = Path.Combine(root, "AgentDock.exe");
        if (enabled && (!File.Exists(launcher) || !File.Exists(Path.Combine(root, "install", "current.json"))))
            throw new DockException("DesktopMissing", "请先安装 AgentDock，再设置登录自启。");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("AgentDock", "\"" + launcher + "\" --launch --tray --root \"" + root + "\"");
        else key.DeleteValue("AgentDock", false);
    }
}
