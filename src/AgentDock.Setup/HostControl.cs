using System.Text.Json;
using AgentDock.Core;

namespace AgentDock.Setup;

internal sealed record HostProbe(bool Alive, HostStatus? Status, string? Error);

/// <summary>
/// 通过共享 DockClient 与后台通信。后台未运行（管道超时/断开）视为“不在运行”，
/// 此时改由 <see cref="RequireNoPersistedUnfinished"/> 直接读取持久化任务，排队/运行/暂停任一存在即拒绝；
/// 后台有响应但状态异常时 fail closed，绝不在状态不明时继续更新/安装/卸载。
/// </summary>
internal static class HostControl
{
    public static HostProbe Probe(InstallLayout layout)
    {
        var client = new DockClient(home: layout.DataDir);
        try
        {
            var status = client.InvokeAsync<HostStatus>("status").GetAwaiter().GetResult();
            return new HostProbe(true, status, null);
        }
        catch (TimeoutException)
        {
            return new HostProbe(false, null, null);
        }
        catch (IOException)
        {
            return new HostProbe(false, null, null);
        }
        catch (DockException ex)
        {
            return new HostProbe(true, null, $"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new HostProbe(true, null, ex.Message);
        }
    }

    /// <summary>
    /// 后台离线（命名管道不存在）时的 fail-closed 门禁：直接读取持久化任务数。
    /// 排队/运行/暂停任一存在即拒绝；tasks.json 损坏、schema 不识或读取失败同样拒绝（状态不明不得继续）。
    /// </summary>
    public static void RequireNoPersistedUnfinished(InstallLayout layout, Action<string> log)
    {
        int count;
        try
        {
            count = TaskService.CountPersistedUnfinished(layout.DataDir);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var tasksPath = Path.Combine(layout.DataDir, "tasks.json");
            throw new InvalidOperationException(
                $"无法读取持久化任务状态（{tasksPath}）：{ex.Message}。状态不明时拒绝继续（fail closed）。", ex);
        }
        if (count > 0)
        {
            throw new InvalidOperationException(
                $"后台未运行，但存在 {count} 个未完成任务（排队/运行/暂停）；更新/安装/卸载都不得绕过它们。" +
                "请先启动后台确认任务状态后再重试。");
        }
        log("后台未运行；持久化任务检查通过（未完成任务 0 个）。");
    }

    /// <summary>请求后台正常退出（confirm=false：静默、无对话框），并等待管道关闭。</summary>
    public static void Shutdown(InstallLayout layout)
    {
        var client = new DockClient(home: layout.DataDir);
        client.InvokeAsync<JsonElement>("shutdown", new { confirm = false }).GetAwaiter().GetResult();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (!Probe(layout).Alive) return;
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("后台未在 20 秒内退出，操作已中止。");
    }
}
