using AgentDock.Core;

namespace AgentDock.Desktop;

/// <summary>本机管道协议的强类型封装。每次调用一个请求，自动重连与去重由 AppState 负责。</summary>
public sealed class HostApi(DockClient client)
{
    public DockClient Client { get; } = client;

    public Task EnsureHostAsync(CancellationToken ct) => Client.EnsureHostAsync(ct);

    public Task<HostStatus> StatusAsync(CancellationToken ct) =>
        Client.InvokeAsync<HostStatus>("status", null, ct);

    public Task<TaskSummary[]> ListAsync(string? project, CancellationToken ct) =>
        Client.InvokeAsync<TaskSummary[]>("list", new { project }, ct);

    public Task<TaskDetail> GetAsync(string id, long after, CancellationToken ct) =>
        Client.InvokeAsync<TaskDetail>("get", new { id, after }, ct);

    public Task<TaskSummary> StopAsync(string id, CancellationToken ct) =>
        Client.InvokeAsync<TaskSummary>("stop", new { id }, ct);

    public Task<ConfigurationView> GetConfigurationAsync(CancellationToken ct) =>
        Client.InvokeAsync<ConfigurationView>("config.get", null, ct);

    public Task<ConfigurationView> SetConfigurationAsync(DockConfiguration configuration, CancellationToken ct) =>
        Client.InvokeAsync<ConfigurationView>("config.set", new { configuration }, ct);

    public Task<ConfigurationView> SetCredentialAsync(string key, CancellationToken ct) =>
        Client.InvokeAsync<ConfigurationView>("credential.set", new { key }, ct);

    public Task<CleanupPreview> CleanupPreviewAsync(string scope, string? project, CancellationToken ct) =>
        Client.InvokeAsync<CleanupPreview>("cleanup.preview", new { scope, project }, ct);

    public Task<CleanupPreview> CleanupApplyAsync(string scope, string? project, string revision, CancellationToken ct) =>
        Client.InvokeAsync<CleanupPreview>("cleanup.apply", new { scope, project, revision }, ct);

    public Task<HostStatus> ShutdownAsync(bool confirm, CancellationToken ct) =>
        Client.InvokeAsync<HostStatus>("shutdown", new { confirm }, ct);
}
