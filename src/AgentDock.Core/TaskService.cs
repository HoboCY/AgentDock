using System.Security.Cryptography;
using System.Text.Json;

namespace AgentDock.Core;

/// <summary>One durable task owner. Windows are clients; reconnects never schedule work.</summary>
public sealed class TaskService : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly string file;
    private readonly FileStream ownership;
    private readonly IExecutionEngine engine;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, RunningTask> running = [];
    private TaskCompletionSource changed = NewSignal();
    private ServiceState state;
    private bool stopping;

    public TaskService(string home, IExecutionEngine engine, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(home);
        this.engine = engine;
        this.clock = clock ?? TimeProvider.System;
        file = Path.Combine(home, "tasks.json");
        ownership = new FileStream(Path.Combine(home, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            state = AtomicJson.Read<ServiceState>(file);
            if (state.Schema != 1) throw new InvalidDataException("不支持的任务存储版本。");
            ValidateConfiguration(state.Configuration);
            foreach (var task in state.Tasks.Values)
            {
                if (task.Status == TaskStatus.Running)
                {
                    task.Status = TaskStatus.Interrupted;
                    task.FinishedAt = this.clock.GetUtcNow();
                    task.Error = "后台异常退出；已开始的任务不会自动重跑。请使用新的 request_id 明确重新派发。";
                    state.Requests[task.RequestId].Status = task.Status;
                    AddEvent(task, "interrupted", task.Error);
                }
                else if (task.Status == TaskStatus.Queued)
                {
                    task.Status = TaskStatus.Paused;
                    AddEvent(task, "paused", "后台恢复后队列暂停；需要 Codex 明确恢复。");
                }
            }
            Save();
        }
        catch { ownership.Dispose(); throw; }
    }

    public DockConfiguration Configuration
    {
        get { lock (gate) return Copy(state.Configuration); }
    }

    public static int CountPersistedUnfinished(string home)
    {
        var snapshot = AtomicJson.Read<ServiceState>(Path.Combine(home, "tasks.json"));
        if (snapshot.Schema != 1) throw new InvalidDataException("不支持的任务存储版本，不能判断升级是否安全。");
        return snapshot.Tasks.Values.Count(t => t.Status.IsUnfinished());
    }

    public void Configure(DockConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        lock (gate)
        {
            EnsureAccepting();
            state.Configuration = Copy(configuration);
            Save();
            Schedule();
        }
    }

    public static void ValidateConfiguration(DockConfiguration configuration)
    {
        if (configuration.MaxConcurrency is < 1 or > 32 || !double.IsFinite(configuration.OverlayOpacity) ||
            configuration.OverlayOpacity is < .3 or > 1 || configuration.Roles is null ||
            !Roles.All.Order().SequenceEqual(configuration.Roles.Keys.Order()) ||
            configuration.Roles.Values.Any(x => x is null || x.TimeoutSeconds is < 1 or > 86400 ||
                string.IsNullOrWhiteSpace(x.Prompt) || x.Prompt.Length > 64000))
            throw new DockException("InvalidConfiguration", "需要五个固定角色；并发 1–32，超时 1–86400 秒，透明度 0.3–1。");
    }

    public TaskSummary Start(TaskRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length is < 8 or > 100 ||
            !Roles.All.Contains(request.Role) || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 300 ||
            string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 200000)
            throw new DockException("InvalidRequest", "请求需包含 8–100 字符 request_id、项目、标题、固定角色和任务正文。");
        // Normalization is lexical for retries so a deleted project cannot make an old request executable again.
        if (!Path.IsPathFullyQualified(request.Cwd)) throw new DockException("InvalidProject", "需要绝对项目路径。");
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Cwd));
        request = request with { Cwd = OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonDefaults.Options)));
        lock (gate)
        {
            if (state.Requests.TryGetValue(request.RequestId, out var previous))
            {
                if (previous.Fingerprint != fingerprint) throw new DockException("RequestConflict", "request_id 已被不同参数使用。");
                return state.Tasks.TryGetValue(previous.Id, out var existing) ? Summary(existing) : Retired(request.RequestId, previous);
            }
            EnsureAccepting();
            project = ProjectPaths.Normalize(project);
            var task = new StoredTask
            {
                Id = Guid.NewGuid().ToString("N"), RequestId = request.RequestId, Project = project, Title = request.Title,
                Role = request.Role, Prompt = request.Prompt, Configuration = state.Configuration.Roles[request.Role],
                Settings = Copy(state.Configuration), AcceptedAt = clock.GetUtcNow()
            };
            state.Tasks.Add(task.Id, task);
            state.Requests.Add(request.RequestId, new Tombstone { Id = task.Id, Fingerprint = fingerprint });
            AddEvent(task, "accepted", "任务已接受，等待可用执行名额。");
            Save(); // Commit identity before any execution can escape.
            Schedule();
            return Summary(task);
        }
    }

    public TaskSummary[] List(string? project = null)
    {
        lock (gate) return state.Tasks.Values.Where(t => project is null || t.Project.Equals(project, ProjectPaths.Comparison))
            .OrderByDescending(t => t.AcceptedAt).Select(Summary).ToArray();
    }

    public TaskDetail Get(string id, long after = 0)
    {
        lock (gate)
        {
            var task = RequireTask(id);
            // Bound a page even when each event reaches the 16k text limit.
            var events = task.Events.Where(e => e.Cursor > after).Take(32).ToArray();
            var cursor = events.Length > 0 ? events[^1].Cursor : task.Cursor;
            return new(Summary(task), task.BodyAvailable ? "available" : "expired", task.Result,
                events, cursor, task.Events.Any(e => e.Cursor > cursor));
        }
    }

    public async Task<TaskDetail> WaitAsync(string id, long after, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (after < 0 || timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(45))
            throw new DockException("InvalidWait", "after 必须非负，等待时间为 0–45 秒。");
        var end = clock.GetTimestamp();
        while (true)
        {
            Task signal;
            lock (gate)
            {
                var task = RequireTask(id);
                if (task.Cursor > after || !task.Status.IsUnfinished() || task.Status == TaskStatus.Paused) return Get(id, after);
                signal = changed.Task;
            }
            var remaining = timeout - clock.GetElapsedTime(end);
            if (remaining <= TimeSpan.Zero) return Get(id, after);
            try { await signal.WaitAsync(remaining, cancellationToken); }
            catch (TimeoutException) { return Get(id, after); }
        }
    }

    public TaskSummary Stop(string id)
    {
        CancellationTokenSource? cancellation = null;
        TaskSummary summary;
        lock (gate)
        {
            var task = RequireTask(id);
            if (running.TryGetValue(id, out var active))
            {
                active.Cancelled = true;
                cancellation = active.Cancellation;
                AddEvent(task, "stopping", "正在停止后续执行；保留已有文件修改。");
            }
            else if (task.Status.IsUnfinished()) Finish(task, TaskStatus.Cancelled, null, null);
            Save();
            summary = Summary(task);
        }
        Cancel(cancellation);
        return summary;
    }

    public TaskSummary Resume(string id)
    {
        lock (gate)
        {
            EnsureAccepting();
            var task = RequireTask(id);
            if (task.Status != TaskStatus.Paused) throw new DockException("NotPaused", "只有尚未执行的暂停任务可以恢复。中断或失败任务需要新的 request_id。");
            ProjectPaths.Normalize(task.Project);
            task.Status = TaskStatus.Queued;
            AddEvent(task, "queued", "Codex 已明确恢复此排队任务。");
            Save();
            Schedule();
            return Summary(task);
        }
    }

    public CleanupPreview Cleanup(bool apply, string scope = "expired", string? project = null, string? expectedRevision = null)
    {
        if (scope is not ("expired" or "bodies" or "all")) throw new DockException("InvalidScope", "清理范围必须是 expired、bodies 或 all。");
        lock (gate)
        {
            if (apply && expectedRevision is not null && Cleanup(false, scope, project).Revision != expectedRevision)
                throw new DockException("CleanupChanged", "可清理的历史已变化，请重新预览后确认。");
            int summaries = 0, bodies = 0, protectedTasks = 0;
            var selection = new List<string>();
            var now = clock.GetUtcNow();
            foreach (var task in state.Tasks.Values.ToArray())
            {
                if (project is not null && !task.Project.Equals(project, ProjectPaths.Comparison)) continue;
                if (task.Status.IsUnfinished()) { protectedTasks++; continue; }
                var removeSummary = scope == "all" || (scope == "expired" && task.FinishedAt <= now.AddDays(-30));
                var removeBody = removeSummary || scope == "bodies" || task.FinishedAt <= now.AddDays(-7);
                if (removeSummary || removeBody && task.BodyAvailable) selection.Add(task.Id + ":" + removeSummary + ":" + task.BodyAvailable);
                if (removeBody && task.BodyAvailable)
                {
                    bodies++;
                    if (apply)
                    {
                        task.BodyAvailable = false; task.Prompt = ""; task.Result = null; task.Events.Clear(); task.Usage.Clear();
                        task.Configuration = new(""); task.Settings = new();
                    }
                }
                if (removeSummary) { summaries++; if (apply) state.Tasks.Remove(task.Id); }
            }
            if (apply) Save();
            var revision = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope + "\n" + project + "\n" + string.Join("\n", selection.Order()))));
            return new(summaries, bodies, protectedTasks, revision);
        }
    }

    public async Task ShutdownAsync(bool confirm)
    {
        Task[] pending;
        CancellationTokenSource[] cancellations;
        lock (gate)
        {
            if (!confirm && state.Tasks.Values.Any(t => t.Status.IsUnfinished()))
                throw new DockException("ConfirmationRequired", "存在未完成任务。完全退出将停止运行任务并取消排队或暂停任务。");
            stopping = true;
            foreach (var task in state.Tasks.Values.Where(t => t.Status is TaskStatus.Queued or TaskStatus.Paused))
                Finish(task, TaskStatus.Cancelled, null, null);
            foreach (var active in running.Values) active.Cancelled = true;
            cancellations = running.Values.Select(x => x.Cancellation).ToArray();
            pending = running.Values.Select(x => x.Completion).ToArray();
            Save();
        }
        foreach (var cancellation in cancellations) Cancel(cancellation);
        await Task.WhenAll(pending);
    }

    public async ValueTask DisposeAsync()
    {
        try { await ShutdownAsync(true); }
        finally { ownership.Dispose(); }
    }

    private void Schedule()
    {
        if (stopping) return;
        foreach (var task in state.Tasks.Values.Where(t => t.Status == TaskStatus.Queued).OrderBy(t => t.AcceptedAt))
        {
            if (running.Count >= state.Configuration.MaxConcurrency) break;
            if (Roles.Writes(task.Role) && running.Keys.Select(id => state.Tasks[id])
                .Any(other => Roles.Writes(other.Role) && ProjectPaths.Overlap(other.Project, task.Project))) continue;
            task.Status = TaskStatus.Running;
            task.StartedAt = clock.GetUtcNow();
            AddEvent(task, "running", "已开始执行。");
            Save();
            var active = new RunningTask();
            running.Add(task.Id, active);
            active.Completion = Task.Run(() => RunAsync(task, active));
        }
    }

    private async Task RunAsync(StoredTask task, RunningTask active)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(task.Configuration.TimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, active.Cancellation.Token);
        ExecutionResult? result = null;
        string? error = null;
        try
        {
            result = await engine.ExecuteAsync(new(task.Id, task.Project, task.Role, task.Prompt, task.Configuration, task.Settings),
                e => Report(task.Id, e), linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (DockException ex) { error = ex.Code + ": " + ex.Message; }
        catch (Exception) { error = "ExecutionFailed: 执行未能完成。详细错误仅记录脱敏信息。"; }
        lock (gate)
        {
            var status = active.Cancelled ? TaskStatus.Cancelled : timeout.IsCancellationRequested ? TaskStatus.TimedOut :
                result?.Success == true ? TaskStatus.Completed : TaskStatus.Failed;
            task.Changes = result?.Changes ?? [];
            Finish(task, status, result?.Result, status == TaskStatus.TimedOut ? "任务达到接收时的执行时限。" : error ?? result?.Error);
            running.Remove(task.Id);
            Save();
            Schedule();
        }
        active.Cancellation.Dispose();
    }

    private void Report(string id, EngineEvent value)
    {
        lock (gate)
        {
            if (!state.Tasks.TryGetValue(id, out var task) || task.Status != TaskStatus.Running) return;
            if (value.Kind == "usage" && !string.IsNullOrWhiteSpace(value.UsageId))
            {
                if (value.InputTokens < 0 || value.OutputTokens < 0 || value.GenerationSeconds is double seconds && (!double.IsFinite(seconds) || seconds <= 0)) return;
                task.Usage.TryGetValue(value.UsageId, out var old);
                var usage = new UsagePart(value.InputTokens ?? old?.Input, value.OutputTokens ?? old?.Output,
                    value.GenerationSeconds ?? old?.Seconds);
                if (old == usage) return;
                task.Usage[value.UsageId] = usage;
                task.InputTokens = SumKnown(task.Usage.Values.Select(x => x.Input));
                task.OutputTokens = SumKnown(task.Usage.Values.Select(x => x.Output));
                task.GenerationTokensPerSecond = usage.Output.HasValue && usage.Seconds > 0 ? usage.Output / usage.Seconds : null;
            }
            AddEvent(task, value.Kind, value.Text.Length > 16000 ? value.Text[..16000] + " [已截断]" : value.Text);
            Save();
        }
    }

    private void Finish(StoredTask task, TaskStatus status, string? result, string? error)
    {
        task.Status = status; task.FinishedAt = clock.GetUtcNow(); task.Result = result; task.Error = error;
        state.Requests[task.RequestId].Status = status;
        AddEvent(task, "finished", status.ToString());
    }
    private static long? SumKnown(IEnumerable<long?> values)
    {
        var parts = values.ToArray();
        return parts.Length > 0 && parts.All(x => x.HasValue) ? parts.Sum(x => x!.Value) : null;
    }
    private StoredTask RequireTask(string id) => state.Tasks.TryGetValue(id, out var task) ? task :
        throw new DockException(state.Requests.Values.Any(x => x.Id == id) ? "SummaryExpired" : "UnknownTask", "任务不存在或其摘要已清理；旧 request_id 不会重新执行。");
    private void EnsureAccepting() { if (stopping) throw new DockException("HostStopping", "后台正在退出，拒绝接收新任务。"); }
    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* Execution finished after the stop request captured its handle. */ }
    }
    private void AddEvent(StoredTask task, string kind, string text)
    {
        task.Events.Add(new(++task.Cursor, clock.GetUtcNow(), kind, text));
        if (task.Events.Count > 2000) task.Events.RemoveRange(0, task.Events.Count - 2000);
    }
    private void Save()
    {
        AtomicJson.Write(file, state);
        var previous = changed; changed = NewSignal(); previous.TrySetResult();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DockConfiguration Copy(DockConfiguration value) => value with { Roles = new(value.Roles) };
    private static TaskSummary Summary(StoredTask t) => new(t.Id, t.RequestId, t.Project, t.Title, t.Role, t.Status, t.AcceptedAt,
        t.StartedAt, t.FinishedAt, t.InputTokens, t.OutputTokens, t.GenerationTokensPerSecond, t.Error, t.BodyAvailable, t.Changes.ToArray());
    private static TaskSummary Retired(string requestId, Tombstone t) => new(t.Id, requestId, "", "摘要已清理", "", t.Status,
        default, null, null, null, null, null, "SummaryExpired: 原任务身份保留，不重新执行。", false, []);

    private sealed class RunningTask
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Cancelled { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    public sealed class ServiceState
    {
        public int Schema { get; set; } = 1;
        public DockConfiguration Configuration { get; set; } = new();
        public Dictionary<string, StoredTask> Tasks { get; set; } = [];
        public Dictionary<string, Tombstone> Requests { get; set; } = [];
    }
    public sealed class Tombstone
    {
        public string Id { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public TaskStatus Status { get; set; }
    }
    public sealed record UsagePart(long? Input, long? Output, double? Seconds);
    public sealed class StoredTask
    {
        public string Id { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Project { get; set; } = "";
        public string Title { get; set; } = "";
        public string Role { get; set; } = "";
        public string Prompt { get; set; } = "";
        public RoleConfiguration Configuration { get; set; } = new("");
        public DockConfiguration Settings { get; set; } = new();
        public TaskStatus Status { get; set; } = TaskStatus.Queued;
        public DateTimeOffset AcceptedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public double? GenerationTokensPerSecond { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
        public bool BodyAvailable { get; set; } = true;
        public FileChange[] Changes { get; set; } = [];
        public List<TaskEvent> Events { get; set; } = [];
        public Dictionary<string, UsagePart> Usage { get; set; } = [];
        public long Cursor { get; set; }
    }
}
