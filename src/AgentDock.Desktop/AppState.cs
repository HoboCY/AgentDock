using System.Globalization;
using AgentDock.Core;
using TaskStatus = AgentDock.Core.TaskStatus;

namespace AgentDock.Desktop;

/// <summary>桌面侧共享状态：单请求轮询、自动重连、状态变化通知。</summary>
public sealed class AppState(HostApi api)
{
    private readonly Dictionary<string, TaskStatus> _lastStatuses = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _pollTask;
    private bool _configLoaded;
    private bool _everConnected;
    private bool _wasReachable;
    private bool _hasTaskSnapshot;

    public HostApi Api { get; } = api;
    public HostStatus? Host { get; private set; }
    public ConfigurationView? Configuration { get; private set; }
    public TaskSummary[] Tasks { get; private set; } = [];
    public bool HostReachable { get; private set; }
    public string? LastError { get; private set; }
    public CancellationToken Token => _cancellation.Token;

    /// <summary>状态或任务列表变化时触发，始终在 UI 线程。</summary>
    public event Action? Changed;

    /// <summary>需要提醒用户的消息（隐藏时也要可见），第二个参数为是否为失败/需处理事项。</summary>
    public event Action<string, bool>? NotifyRequested;

    public string SummaryLine
    {
        get
        {
            var unfinished = Tasks.Count(t => t.Status.IsUnfinished());
            return HostReachable ? $"后台已连接 · 未完成 {unfinished}" : "后台未连接";
        }
    }

    /// <summary>一次轮询不超过一个在途序列；失败后下一轮自动重试连接。</summary>
    public Task PollAsync()
    {
        if (Token.IsCancellationRequested) return Task.CompletedTask;
        return _pollTask is { IsCompleted: false } running ? running : _pollTask = PollCoreAsync();
    }

    private async Task PollCoreAsync()
    {
        var reachable = false;
        try
        {
            Host = await Api.StatusAsync(Token);
            reachable = true;
            LastError = null;
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { return; }
        catch (Exception ex) { Host = null; LastError = Describe(ex); }

        if (reachable)
        {
            try { Tasks = await Api.ListAsync(null, Token); }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested) { LastError = "任务列表请求超时。"; }
            catch (Exception ex) when (ex is not OperationCanceledException) { LastError = Describe(ex); }

            if (!_configLoaded)
            {
                try { Configuration = await Api.GetConfigurationAsync(Token); _configLoaded = true; }
                catch (Exception) { /* 下一轮继续尝试。 */ }
            }
        }

        var reconnected = reachable && _everConnected && !_wasReachable;
        var disconnected = !reachable && _wasReachable;
        HostReachable = reachable;
        _everConnected |= reachable;
        _wasReachable = reachable;
        if (reconnected) NotifyRequested?.Invoke("后台已重新连接。", false);
        if (disconnected) NotifyRequested?.Invoke("后台连接中断，将自动重试。", true);

        if (reachable) DetectTransitions();
        Changed?.Invoke();
    }

    /// <summary>显式启动后台（只有用户操作会调用 EnsureHostAsync），随后立即刷新状态。</summary>
    public async Task<HostStatus?> StartHostAsync()
    {
        try
        {
            await Api.EnsureHostAsync(Token);
            await PollAsync();
            return Host;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = Describe(ex);
            HostReachable = false;
            NotifyRequested?.Invoke(LastError, true);
            Changed?.Invoke();
            return null;
        }
    }

    public void ApplyConfiguration(ConfigurationView view)
    {
        Configuration = view;
        _configLoaded = true;
        Changed?.Invoke();
    }

    public static string Describe(Exception exception) => exception switch
    {
        DockException dock => dock.Message,
        TimeoutException => "后台未运行或响应超时。",
        IOException => "后台未运行或连接已断开。",
        OperationCanceledException => "后台响应超时。",
        _ => "后台请求失败（" + exception.GetType().Name + "）。"
    };

    private void DetectTransitions()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var notifyCompletion = Configuration?.Configuration.NotifyOnCompletion == true;
        foreach (var task in Tasks)
        {
            seen.Add(task.Id);
            if ((_lastStatuses.TryGetValue(task.Id, out var previous) && previous != task.Status) ||
                (!_lastStatuses.ContainsKey(task.Id) && _hasTaskSnapshot))
            {
                switch (task.Status)
                {
                    case TaskStatus.Failed or TaskStatus.TimedOut:
                        NotifyRequested?.Invoke($"任务失败：{task.Title}", true);
                        break;
                    case TaskStatus.Interrupted:
                        NotifyRequested?.Invoke($"任务中断：{task.Title}", true);
                        break;
                    case TaskStatus.Paused:
                        NotifyRequested?.Invoke($"任务等待 Codex 明确恢复：{task.Title}", true);
                        break;
                    case TaskStatus.Completed when notifyCompletion:
                        NotifyRequested?.Invoke($"任务完成：{task.Title}", false);
                        break;
                }
            }
            _lastStatuses[task.Id] = task.Status;
        }
        if (_lastStatuses.Count > seen.Count)
        {
            foreach (var stale in _lastStatuses.Keys.Where(key => !seen.Contains(key)).ToArray())
                _lastStatuses.Remove(stale);
        }
        _hasTaskSnapshot = true;
    }
}

/// <summary>界面显示格式：未知用量一律“待更新”，不估算。</summary>
public static class Fmt
{
    public static string Status(TaskStatus status) => status switch
    {
        TaskStatus.Queued => "排队",
        TaskStatus.Running => "运行中",
        TaskStatus.Paused => "暂停",
        TaskStatus.Interrupted => "中断",
        TaskStatus.Completed => "已完成",
        TaskStatus.Failed => "失败",
        TaskStatus.Cancelled => "已取消",
        TaskStatus.TimedOut => "超时",
        _ => status.ToString()
    };

    public static string Tokens(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "待更新";

    public static string Total(IEnumerable<long?> values)
    {
        var items = values.ToArray();
        if (items.Length == 0) return "0 Token";
        if (items.All(x => x.HasValue)) return Tokens(items.Sum(x => x!.Value)) + " Token";
        if (items.All(x => !x.HasValue)) return "待更新";
        return Tokens(items.Sum(x => x ?? 0)) + " Token（部分待更新）";
    }

    public static string Speed(double? value) =>
        value is { } speed ? speed.ToString("0.0", CultureInfo.CurrentCulture) + " tok/s" : "待更新";

    public static string Elapsed(TaskSummary task, DateTimeOffset now)
    {
        var start = task.AcceptedAt;
        var end = task.FinishedAt ?? now;
        var span = end - start;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : span.ToString(@"m\:ss");
    }
}
