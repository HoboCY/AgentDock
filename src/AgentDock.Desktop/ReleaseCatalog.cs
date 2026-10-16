using System.Net;
using System.Text.Json;

namespace AgentDock.Desktop;

public sealed record ReleaseNotice(bool NewVersion, string Message);

public static class ReleaseCatalog
{
    public const string ReleasesPage = "https://github.com/HoboCY/AgentDock/releases";

    public static async Task<ReleaseNotice> CheckAsync(HttpClient client, string installed, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/HoboCY/AgentDock/releases/latest");
        request.Headers.UserAgent.ParseAdd("AgentDock/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, "尚无公开发行版本。");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(installed, out var current))
            return new(false, "无法比较版本，请查看发行说明。");
        return latest > current ? new(true, $"发现新版本 {tag}。请查看发行说明，下载后由安装程序在无未完成任务时更新。") :
            new(false, "当前版本已是最新公开版本。");
    }
}
