using System.Diagnostics;

namespace AgentDock.Setup;

/// <summary>
/// 安装 / 同版本修复 / 更新 / 卸载的统一文件操作门禁（fail closed）：
///   1. 先写入更新标记 install\update.lock；Host 启动与 start/resume 都会拒绝该标记，
///      因此更新窗口内不会再有新任务被接纳。
///   2. 探测后台：在线且空闲（CanUpdate 且未完成任务为 0）→ 请求正常退出（shutdown confirm=false）；
///      离线（管道不存在）→ 读取持久化任务，排队/运行/暂停任一存在即拒绝；
///      tasks.json 无法解析或版本不识 → 一律拒绝（状态不明不得继续）。
///   3. 确认后台已退出后才独占获取 data\launch.lock（Host 生命周期锁），并持有到整个文件切换结束；
///      绝不先拿 launch.lock 再停后台，避免与启动中的 Host 互相等待形成死锁。
///   4. 释放顺序：先删除自己的更新标记，再释放 launch.lock，防止等待者看到陈旧标记。
///
/// 标记可能先于 launch.lock 出现并被缺席的 Host 看到——那是预期行为：它会让竞争启动的 Host 快速失败。
/// </summary>
internal sealed class OperationGate : IDisposable
{
    private static readonly TimeSpan LaunchLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnparsedMarkerGrace = TimeSpan.FromMinutes(15);

    private readonly InstallLayout _layout;
    private readonly FileStream _launch;
    private readonly string _operation;
    private bool _disposed;

    private OperationGate(InstallLayout layout, FileStream launch, string operation)
    {
        _layout = layout;
        _launch = launch;
        _operation = operation;
    }

    /// <summary>操作真正完成时由调用方置为 true（仅影响日志措辞）。</summary>
    public bool Completed { get; set; }

    public static OperationGate Enter(
        InstallLayout layout, string targetVersion, string operation, Action<string> log, string? payloadSha256 = null)
    {
        Directory.CreateDirectory(layout.InstallDir);
        ClearOrRefuseForeignMarker(layout, log, operation);

        layout.WriteJsonAtomic(layout.UpdateLockPath, new UpdateLock(
            Schema: 1,
            TargetVersion: targetVersion,
            ProcessId: Environment.ProcessId,
            CreatedAt: DateTimeOffset.UtcNow.ToString("o"),
            PayloadSha256: payloadSha256,
            Operation: operation));
        var written = SetupJson.Deserialize<UpdateLock>(File.ReadAllText(layout.UpdateLockPath));
        if (written.ProcessId != Environment.ProcessId)
        {
            // 另一个安装/更新进程刚覆盖了标记：实际文件切换仍由 launch.lock 串行化，这里保守拒绝。
            throw new InvalidOperationException(
                $"[{operation}] 更新标记被其他进程覆盖（PID {written.ProcessId}），为安全起见拒绝继续。");
        }
        log($"[{operation}] 已写入更新标记：{layout.UpdateLockPath}");

        FileStream? launch = null;
        var locked = false;
        try
        {
            // 门禁一：停止空闲后台（在线）或离线复核持久化任务。必须在拿 launch.lock 之前完成。
            StopHostIfRunning(layout, log, operation);
            // 门禁二：独占 Host 生命周期锁，贯穿整个文件切换。
            launch = AcquireLaunchLock(layout, log, operation);
            // 持锁期间后台不可能运行；再次离线复核，防止停机与拿锁之间出现状态变化。
            HostControl.RequireNoPersistedUnfinished(layout, log);
            locked = true;
            var gate = new OperationGate(layout, launch, operation);
            launch = null;
            return gate;
        }
        finally
        {
            if (!locked)
            {
                try { launch?.Dispose(); } catch { /* 释放失败不影响异常传播。 */ }
                TryDeleteOwnMarker(layout);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 先删除标记再释放 launch.lock：等待者拿到锁后不会再看到本次操作的陈旧标记。
        TryDeleteOwnMarker(_layout);
        try { _launch.Dispose(); } catch { /* 进程退出时由 OS 回收。 */ }
        // 卸载会删除 install 目录；此时不得再写 setup.log，否则会把已删除的目录重新创建出来。
        if (Directory.Exists(_layout.InstallDir))
        {
            _layout.Log($"[{_operation}] 文件操作门禁已释放（{(Completed ? "完成" : "未确认完成，标记已清理")}）。");
        }
    }

    /// <summary>已有标记：由存活进程持有则拒绝；确认持有者已退出才清理；无法解析按新旧保守处理。</summary>
    private static void ClearOrRefuseForeignMarker(InstallLayout layout, Action<string> log, string operation)
    {
        var path = layout.UpdateLockPath;
        if (!File.Exists(path)) return;

        UpdateLock? marker = null;
        try
        {
            marker = SetupJson.Deserialize<UpdateLock>(File.ReadAllText(path));
        }
        catch
        {
            marker = null;
        }

        if (marker is not null)
        {
            if (marker.ProcessId == Environment.ProcessId)
            {
                log($"[{operation}] 清理本进程遗留的更新标记。");
                TryDelete(path);
                return;
            }
            if (IsProcessAlive(marker.ProcessId))
            {
                throw new InvalidOperationException(
                    $"[{operation}] 另一个 AgentDock 安装/更新流程正在进行（PID {marker.ProcessId}，标记：{path}）。" +
                    "请等待其结束；若确认该进程已崩溃，请手动删除该标记后重试。");
            }
            log($"[{operation}] 清理失效的更新标记（原进程 PID {marker.ProcessId} 已退出）。");
            TryDelete(path);
            return;
        }

        // 无法解析：新鲜文件视为可疑并拒绝；超过宽限期视为崩溃残留清理。
        TimeSpan age;
        try
        {
            age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            age = TimeSpan.Zero;
        }
        if (age < UnparsedMarkerGrace)
        {
            throw new InvalidOperationException(
                $"[{operation}] 更新标记无法解析且仍很新：{path}。为安全起见拒绝继续；请稍后重试或手动删除该文件。");
        }
        log($"[{operation}] 清理无法解析且已过期的更新标记（{age.TotalMinutes:F0} 分钟前）。");
        TryDelete(path);
    }

    /// <summary>后台在线且空闲时请求正常退出；离线时由持久化状态决定是否放行。</summary>
    private static void StopHostIfRunning(InstallLayout layout, Action<string> log, string operation)
    {
        var probe = HostControl.Probe(layout);
        if (!probe.Alive)
        {
            HostControl.RequireNoPersistedUnfinished(layout, log);
            return;
        }
        if (probe.Error is not null)
        {
            throw new InvalidOperationException($"[{operation}] 后台状态异常（{probe.Error}），为安全起见拒绝继续。");
        }
        var status = probe.Status!;
        if (!status.CanUpdate || status.UnfinishedTasks > 0)
        {
            throw new InvalidOperationException(
                $"[{operation}] 后台有排队/运行/暂停中的任务（{status.UnfinishedTasks} 个），拒绝继续。" +
                "请在主面板确认所有任务结束后重试。");
        }
        log($"[{operation}] 后台空闲（PID {status.ProcessId}），请求正常退出...");
        HostControl.Shutdown(layout);
        log($"[{operation}] 后台已退出。");
    }

    /// <summary>后台确认退出后独占 launch.lock；期间若再有后台被拉起，先让它空闲退出再重试。</summary>
    private static FileStream AcquireLaunchLock(InstallLayout layout, Action<string> log, string operation)
    {
        Directory.CreateDirectory(layout.DataDir);
        var deadline = DateTime.UtcNow + LaunchLockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(layout.LaunchLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"[{operation}] 无法在 {LaunchLockTimeout.TotalSeconds:F0} 秒内独占后台启动锁：{layout.LaunchLockPath}。" +
                        "可能仍有后台正在启动或退出；为安全起见拒绝继续。", ex);
                }
                // 竞态启动的 Host 会看到更新标记后自行退出；若它仍空闲则再请求一次退出。
                var probe = HostControl.Probe(layout);
                if (probe.Alive && probe.Error is null && probe.Status is { CanUpdate: true, UnfinishedTasks: 0 })
                {
                    try
                    {
                        HostControl.Shutdown(layout);
                    }
                    catch (InvalidOperationException)
                    {
                        // 下一轮重试继续判断。
                    }
                }
                Thread.Sleep(250);
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // 无法判断（权限等）时保守视为存活，拒绝清理标记。
            return true;
        }
    }

    private static void TryDeleteOwnMarker(InstallLayout layout)
    {
        try
        {
            if (!File.Exists(layout.UpdateLockPath)) return;
            var marker = SetupJson.Deserialize<UpdateLock>(File.ReadAllText(layout.UpdateLockPath));
            if (marker.ProcessId == Environment.ProcessId)
            {
                File.Delete(layout.UpdateLockPath);
            }
        }
        catch
        {
            // 无法解析或删除失败：交给下一次安装按保守规则处理。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败会在后续流程继续触发保守检查。
        }
    }
}
