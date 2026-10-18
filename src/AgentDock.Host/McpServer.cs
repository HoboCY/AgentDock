using System.Text.Json;
using AgentDock.Core;

namespace AgentDock.Host;

public sealed class McpServer(DockClient client)
{
    private readonly SemaphoreSlim outputLock = new(1);
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        var active = new List<Task>();
        while (await Wire.ReadLineAsync(input, cancellationToken) is { } line)
        {
            active.RemoveAll(x => x.IsCompleted);
            if (active.Count >= 32) await Task.WhenAny(active);
            active.Add(RespondAsync(line, output, cancellationToken));
        }
        await Task.WhenAll(active);
    }

    private async Task RespondAsync(string line, TextWriter output, CancellationToken cancellationToken)
    {
        object? id = null;
        object? response;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var request = doc.RootElement;
            if (!request.TryGetProperty("id", out var requestId)) return;
            id = requestId.Clone();
            var method = request.GetProperty("method").GetString();
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-06-18", capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "AgentDock", version = Program.Version }, instructions = "Tasks run in a separate background host. Reuse request_id only for identical retries. Failed/interrupted tasks require a new explicit request. Closing this connection never stops tasks. Configure credentials in the desktop panel." },
                "ping" => new { },
                "tools/list" => new { tools = Tools() },
                "tools/call" => await CallAsync(request.GetProperty("params"), cancellationToken),
                _ => throw new DockException("MethodNotFound", "Unsupported MCP method")
            };
            response = new { jsonrpc = "2.0", id, result };
        }
        catch (Exception ex)
        {
            response = new { jsonrpc = "2.0", id, error = new { code = ex is JsonException ? -32700 : -32601, message = "Invalid or unsupported MCP request" } };
        }
        await outputLock.WaitAsync(cancellationToken);
        try { await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonDefaults.Options)); await output.FlushAsync(cancellationToken); }
        finally { outputLock.Release(); }
    }

    private async Task<object> CallAsync(JsonElement call, CancellationToken cancellationToken)
    {
        try
        {
            var name = call.GetProperty("name").GetString();
            var args = call.TryGetProperty("arguments", out var value) ? value : JsonSerializer.SerializeToElement(new { });
            var method = name switch
            {
                "status" => "status", "start_task" => "start", "list_tasks" => "list", "get_task" => "get",
                "wait_task" => "wait", "stop_task" => "stop", "resume_task" => "resume",
                _ => throw new DockException("UnknownTool", "未知工具。")
            };
            object parameters = args;
            if (name == "start_task") parameters = new TaskRequest(args.GetProperty("request_id").GetString()!, args.GetProperty("cwd").GetString()!,
                args.GetProperty("title").GetString()!, args.GetProperty("role").GetString()!, args.GetProperty("prompt").GetString()!);
            if (name == "wait_task") parameters = new { id = args.GetProperty("id").GetString(), after = args.TryGetProperty("after", out var after) ? after.GetInt64() : 0,
                waitSeconds = args.TryGetProperty("wait_seconds", out var wait) ? wait.GetInt32() : 30 };
            await client.EnsureHostAsync(cancellationToken);
            var result = await client.InvokeAsync<JsonElement>(method, parameters, cancellationToken);
            return new { content = new[] { new { type = "text", text = result.GetRawText() } }, structuredContent = new { result }, isError = false };
        }
        catch (Exception ex)
        {
            var (code, message) = ex switch
            {
                DockException dock => (dock.Code, dock.Message),
                TimeoutException => ("HostConnectionTimeout", "后台连接等待超时；此次调用尚未发送，请稍后查询原任务状态。"),
                OperationCanceledException when cancellationToken.IsCancellationRequested => ("Cancelled", "调用已取消；已有任务不会因此自动停止或重新执行。"),
                OperationCanceledException => ("HostResponseTimeout", "等待后台响应超时；请查询原任务状态，不要用新请求重复派发。"),
                IOException => ("HostTransportError", "后台通信中断；请查询原任务状态，不要用新请求重复派发。"),
                _ => ("Unavailable", "后台不可达或参数无效；重连不会重新执行原任务。")
            };
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { code, message }) } }, isError = true };
        }
    }

    private static object[] Tools()
    {
        object Field(string description, string type = "string") => new { type, description };
        object Tool(string name, string description, Dictionary<string, object> fields, string[] required, bool readOnly = true) =>
            new { name, description, inputSchema = new { type = "object", properties = fields, required, additionalProperties = false },
                annotations = new { readOnlyHint = readOnly, destructiveHint = false, idempotentHint = true, openWorldHint = !readOnly } };
        return [
            Tool("status", "Get background version, unfinished count, idle-update readiness and executionProfile (provider, model, reasoningEffort) for the next dispatch. Starts the background on demand.", [], []),
            Tool("start_task", "Accept an external task. Default global concurrency 4; excess queues. Same request_id and parameters reuse the task forever, including after history cleanup. Role/timeouts are captured at acceptance; failed/interrupted tasks NEVER auto-retry. Specify owned files and acceptance criteria in prompt.",
                new() { ["request_id"] = Field("Unique 8–100 character request identity"), ["cwd"] = Field("Existing absolute project directory; no automatic worktree"),
                    ["title"] = Field("Short task title"), ["role"] = new { type = "string", @enum = Roles.All }, ["prompt"] = Field("Bounded work, explicit file ownership and acceptance criteria") },
                ["request_id", "cwd", "title", "role", "prompt"], false),
            Tool("list_tasks", "List summaries, optionally filtered by absolute project path. Unknown metrics are null.", new() { ["project"] = Field("Absolute project path") }, []),
            Tool("get_task", "Get task result and event page. bodyState=expired means the 7-day body has been cleaned; unknown and summary-expired are distinct errors.",
                new() { ["id"] = Field("Task ID"), ["after"] = Field("Last consumed event cursor", "integer") }, ["id"]),
            Tool("wait_task", "Wait up to 45 seconds. Consume nextCursor, drain hasMore; a running task is not completed. Reconnection is read-only.",
                new() { ["id"] = Field("Task ID"), ["after"] = Field("Last consumed event cursor", "integer"), ["wait_seconds"] = Field("0–45; default 30", "integer") }, ["id"]),
            Tool("stop_task", "Stop subsequent execution; preserves existing file changes and never rolls back. Poll until terminal before assuming processes have stopped.", new() { ["id"] = Field("Task ID") }, ["id"], false),
            Tool("resume_task", "Explicitly resume a paused, never-started task after host recovery with its original configuration. Does not restart failed or interrupted tasks.", new() { ["id"] = Field("Paused task ID") }, ["id"], false)
        ];
    }
}
