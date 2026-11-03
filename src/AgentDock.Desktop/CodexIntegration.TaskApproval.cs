using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentDock.Desktop;

public sealed partial class CodexIntegration
{
    private const string PluginId = "agentdock@agentdock-local";
    private const string ApprovalKey = "plugins." + PluginId + ".mcp_servers.agentdock.tools.start_task.approval_mode";

    public Task<bool> InspectTaskApprovalAsync(CancellationToken ct) => TaskApprovalAsync(null, ct);

    // Called only by the guide's explicit authorize/revoke actions, never registration or startup.
    public Task<bool> SetTaskApprovalAsync(bool approved, CancellationToken ct) => TaskApprovalAsync(approved, ct);

    private async Task<bool> TaskApprovalAsync(bool? approved, CancellationToken ct)
    {
        RefreshExecutable();
        if (Executable is null || !File.Exists(Executable)) throw new InvalidOperationException("请先选择 Codex 程序文件。");
        Directory.CreateDirectory(CodexHome);
        var start = CreateStartInfo(["app-server", "--listen", "stdio://"]);
        start.RedirectStandardInput = true;
        start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = new UTF8Encoding(false);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var stderr = process.StandardError.ReadToEndAsync(token);
        var nextId = 0;
        try
        {
            async Task<JsonElement> CallAsync(string method, object parameters)
            {
                var id = ++nextId;
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    var response = document.RootElement;
                    if (!response.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
                        !responseId.TryGetInt32(out var value) || value != id) continue;
                    if (response.TryGetProperty("error", out _))
                        throw new InvalidOperationException("Codex 未能读取或保存任务授权。请重新检查；旧版 Codex 需要更新后重试。");
                    return response.GetProperty("result").Clone();
                }
                throw new InvalidOperationException("Codex 配置连接已关闭，未确认任务授权。请重新检查。");
            }

            await CallAsync("initialize", new { clientInfo = new { name = "agentdock_setup", version = "1.0" } });
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            var snapshot = await CallAsync("config/read", new { includeLayers = true });
            if (approved is null) return TaskStartApproved(snapshot.GetProperty("config"));

            var configPath = Path.Combine(CodexHome, "config.toml");
            var userLayer = snapshot.GetProperty("layers").EnumerateArray().FirstOrDefault(layer =>
                layer.GetProperty("name").GetProperty("type").GetString() == "user" &&
                SamePath(layer.GetProperty("name").GetProperty("file").GetString()!, configPath));
            if (userLayer.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("未找到 Codex 用户配置版本，未修改授权。请重新检查。");
            var mode = approved.Value ? "approve" : "prompt";
            var written = await CallAsync("config/batchWrite", new
            {
                filePath = configPath, expectedVersion = userLayer.GetProperty("version").GetString(),
                edits = new[] { new { keyPath = ApprovalKey, value = mode, mergeStrategy = "upsert" } }
            });
            if (written.GetProperty("status").GetString() != "ok")
                throw new InvalidOperationException("任务授权已保存，但被更高优先级配置覆盖。请在 Codex 中检查组织或项目策略。");
            var check = await CallAsync("config/read", new { includeLayers = false });
            var actual = TaskStartApproved(check.GetProperty("config"));
            if (actual != approved.Value)
                throw new InvalidOperationException("任务授权未通过回读检查。请检查 Codex 中 AgentDock 的工具限制或组织策略。");
            return actual;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Codex 授权操作已取消或超时，请重新检查已保存的状态。");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or IOException)
        {
            throw new InvalidOperationException("无法确认 Codex 任务授权。请更新 Codex 后重新检查；未确认前仍可能需要逐次审批。");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await stderr; } catch (OperationCanceledException) { }
        }
    }

    internal static bool TaskStartApproved(JsonElement config)
    {
        if (!config.TryGetProperty("plugins", out var plugins) ||
            !plugins.TryGetProperty(PluginId, out var plugin) ||
            plugin.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False ||
            !plugin.TryGetProperty("mcp_servers", out var servers) ||
            !servers.TryGetProperty("agentdock", out var server)) return false;
        if (server.TryGetProperty("enabled", out enabled) && enabled.ValueKind == JsonValueKind.False ||
            server.TryGetProperty("enabled_tools", out var allowed) && allowed.ValueKind == JsonValueKind.Array && !allowed.EnumerateArray().Any(tool => tool.GetString() == "start_task") ||
            server.TryGetProperty("disabled_tools", out var denied) && denied.ValueKind == JsonValueKind.Array && denied.EnumerateArray().Any(tool => tool.GetString() == "start_task")) return false;
        if (server.TryGetProperty("tools", out var tools) && tools.TryGetProperty("start_task", out var task) &&
            task.TryGetProperty("approval_mode", out var mode) && mode.ValueKind == JsonValueKind.String)
            return mode.GetString() == "approve";
        return server.TryGetProperty("default_tools_approval_mode", out mode) && mode.GetString() == "approve";
    }
}
