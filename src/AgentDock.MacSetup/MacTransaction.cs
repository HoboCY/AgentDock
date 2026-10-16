namespace AgentDock.MacSetup;

/// <summary>
/// 一次已完成的目录替换：原目录（若存在）已移动到 workspace 内的备份位置，新目录已就位。
/// 在 current.json 成功发布前调用 <see cref="Rollback"/> 恢复原目录；发布成功后 <see cref="Commit"/> 删除备份。
/// 只有 Commit / Rollback 二者之一会真正生效（幂等）。
/// </summary>
internal sealed class PendingSwap
{
    private readonly string _destination;
    private readonly string? _backup;
    private readonly bool _backupIsFile;
    private bool _finished;

    private PendingSwap(string destination, string? backup, bool backupIsFile)
    {
        _destination = destination;
        _backup = backup;
        _backupIsFile = backupIsFile;
    }

    /// <summary>把 staged 移动到 destination；destination 已存在时先备份（移动失败会立即还原）。</summary>
    public static PendingSwap Replace(string staged, string destination, string workspace)
    {
        string? backup = null;
        var backupIsFile = false;
        if (Directory.Exists(destination))
        {
            backup = Path.Combine(workspace, ".backup-" + Guid.NewGuid().ToString("N"));
            MacFileSystem.MoveDirectory(destination, backup);
        }
        else if (File.Exists(destination))
        {
            backup = Path.Combine(workspace, ".backup-" + Guid.NewGuid().ToString("N"));
            File.Move(destination, backup);
            backupIsFile = true;
        }
        try
        {
            MacFileSystem.MoveDirectory(staged, destination);
        }
        catch
        {
            if (backup is not null)
            {
                if (backupIsFile) File.Move(backup, destination);
                else MacFileSystem.MoveDirectory(backup, destination);
            }
            throw;
        }
        return new PendingSwap(destination, backup, backupIsFile);
    }

    /// <summary>发布成功：删除备份（删除失败时留下备份，不做危险清理）。</summary>
    public void Commit()
    {
        if (_finished) return;
        _finished = true;
        if (_backup is not null) MacFileSystem.TryDelete(_backup);
    }

    /// <summary>发布前失败：删除新目录并还原备份；还原失败会抛出（绝不静默声称已回滚）。</summary>
    public void Rollback()
    {
        if (_finished) return;
        _finished = true;
        if (_backup is null)
        {
            MacFileSystem.TryDelete(_destination);
            return;
        }
        MacFileSystem.TryDelete(_destination);
        if (_backupIsFile) File.Move(_backup, _destination, overwrite: true);
        else MacFileSystem.MoveDirectory(_backup, _destination);
    }
}

/// <summary>文件内容快照：用于 current.json 发布前恢复 machine.json 等小清单。</summary>
internal sealed class FileSnapshot
{
    private readonly string _path;
    private readonly byte[]? _content;

    private FileSnapshot(string path, byte[]? content)
    {
        _path = path;
        _content = content;
    }

    public static FileSnapshot Capture(string path) =>
        new(path, File.Exists(path) ? File.ReadAllBytes(path) : null);

    public void Restore()
    {
        if (_content is null)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        File.WriteAllBytes(_path, _content);
    }
}

/// <summary>回滚步骤执行器：逐项捕获失败，任一失败时抛出包含全部失败原因的聚合错误。</summary>
internal static class MacRollback
{
    public static void Run(Exception original, params (string Name, Action Action)[] steps)
    {
        var failures = new List<string>();
        foreach (var (name, action) in steps)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failures.Add($"{name}：{error.Message}");
            }
        }
        if (failures.Count > 0)
        {
            throw new IOException($"{original.Message}；回滚未完全成功：{string.Join("；", failures)}", original);
        }
    }
}
