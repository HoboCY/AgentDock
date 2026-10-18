namespace AgentDock.Core;

public static class Roles
{
    public static readonly string[] All = ["default", "explorer", "worker", "reviewer", "verifier"];
    public static bool Writes(string role) => role is "default" or "worker" or "verifier";
}

public enum TaskStatus { Queued, Running, Paused, Interrupted, Completed, Failed, Cancelled, TimedOut }
public static class TaskStates
{
    public static bool IsUnfinished(this TaskStatus status) => status is TaskStatus.Queued or TaskStatus.Running or TaskStatus.Paused;
}
public sealed record RoleConfiguration(string Prompt, int TimeoutSeconds = 1200);
public sealed record DockConfiguration
{
    public int MaxConcurrency { get; init; } = 4;
    public Dictionary<string, RoleConfiguration> Roles { get; init; } = Core.Roles.All.ToDictionary(x => x,
        x => new RoleConfiguration(RolePrompts.For(x)));
    public string EnginePath { get; init; } = "";
    public string SandboxPath { get; init; } = "";
    public string ShellPath { get; init; } = "";
    public bool NotifyOnCompletion { get; init; }
    public bool StartAtLogin { get; init; }
    public bool OverlayTopmost { get; init; } = true;
    public double OverlayOpacity { get; init; } = .88;
}
public sealed record ConfigurationView(DockConfiguration Configuration, bool CredentialConfigured, string[] Warnings);
public sealed record TaskRequest(string RequestId, string Cwd, string Title, string Role, string Prompt);
public sealed record FileChange(string Path, string? BeforeSha256, string? AfterSha256);
public sealed record TaskSummary(string Id, string RequestId, string Project, string Title, string Role,
    TaskStatus Status, DateTimeOffset AcceptedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    long? InputTokens, long? OutputTokens, double? GenerationTokensPerSecond, string? Error, bool BodyAvailable,
    FileChange[] Changes);
public sealed record TaskEvent(long Cursor, DateTimeOffset At, string Kind, string Text);
public sealed record TaskDetail(TaskSummary Summary, string BodyState, string? Result, TaskEvent[] Events,
    long NextCursor, bool HasMore);
public sealed record CleanupPreview(int Summaries, int Bodies, int UnfinishedProtected, string? Revision = null);
/// <summary>Provider, model and reasoning effort the engine will use for the next dispatch. Not task history.</summary>
public sealed record ExecutionProfile(string Provider, string Model, string ReasoningEffort);
/// <summary>ExecutionProfile stays optional so status payloads from older hosts deserialize without invented metadata.</summary>
public sealed record HostStatus(string Version, int ProcessId, int UnfinishedTasks, bool CanUpdate,
    ExecutionProfile? ExecutionProfile = null);
public sealed record EngineEvent(string Kind, string Text = "", string? UsageId = null,
    long? InputTokens = null, long? OutputTokens = null, double? GenerationSeconds = null);
public sealed record ExecutionTask(string Id, string Project, string Role, string Prompt,
    RoleConfiguration Configuration, DockConfiguration Settings);
public sealed record ExecutionResult(bool Success, string Result, string? Error = null, FileChange[]? Changes = null);
public interface IExecutionEngine
{
    Task<ExecutionResult> ExecuteAsync(ExecutionTask task, Action<EngineEvent> report, CancellationToken cancellationToken);
}
public sealed class DockException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
