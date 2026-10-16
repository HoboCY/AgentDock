namespace AgentDock.Engine;

/// <summary>
/// Bounded retention of verifier check artifacts after a job reaches a terminal
/// state. The sandbox job directory is deleted on completion; whatever the
/// verifier wrote under <c>runtime/check-artifacts</c> is moved to
/// <c>&lt;home&gt;/artifacts/&lt;taskId&gt;</c> first so build/test evidence stays
/// retrievable with the task body. Nothing else from the job directory (sandbox
/// configuration, launch files, credentials-adjacent data) is retained.
///
/// Retention is bounded: empty or oversized check outputs are discarded, and
/// only <see cref="MaxTasks"/> task directories within <see cref="MaxTotalBytes"/>
/// are kept, oldest first. Host retention and manual body cleanup remove matching
/// artifact directories; locked files are retried on the next maintenance pass.
/// </summary>
internal static class CheckArtifactsStore
{
    private static readonly object Gate = new();
    public const long MaxTaskBytes = 64L * 1024 * 1024;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    public const int MaxTasks = 50;

    /// <summary>
    /// Moves the check-artifacts directory under the home artifacts root when it
    /// is non-empty and within bounds. Returns the retained path, or null when
    /// nothing was retained; all failures are best effort.
    /// </summary>
    public static string? Retain(string home, string taskId, string checkArtifactsDirectory) =>
        Retain(home, taskId, checkArtifactsDirectory, MaxTaskBytes);

    internal static string? Retain(string home, string taskId, string checkArtifactsDirectory, long maxTaskBytes)
    {
        lock (Gate)
        try
        {
            if (!Directory.Exists(checkArtifactsDirectory))
            {
                return null;
            }
            var size = Measure(checkArtifactsDirectory);
            if (size is null or 0 || size > maxTaskBytes)
            {
                return null;
            }
            var root = Path.Combine(Path.GetFullPath(home), "artifacts");
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, ExecutionSetup.SafeName(taskId));
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(checkArtifactsDirectory, target);
            Prune(root);
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort: the job directory cleanup continues.
            return null;
        }
    }

    public static void Cleanup(string home, IEnumerable<string> keepTaskIds)
    {
        lock (Gate)
        {
            var root = Path.Combine(Path.GetFullPath(home), "artifacts");
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            var keep = keepTaskIds.Select(ExecutionSetup.SafeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in new DirectoryInfo(root).GetDirectories())
            {
                if (keep.Contains(directory.Name)) continue;
                try { directory.Delete(true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void Prune(string root)
    {
        var tasks = new DirectoryInfo(root).GetDirectories().OrderBy(info => info.LastWriteTimeUtc).ToArray();
        var sizes = new long[tasks.Length];
        var total = 0L;
        for (var index = 0; index < tasks.Length; index++)
        {
            sizes[index] = Measure(tasks[index].FullName) ?? 0;
            total += sizes[index];
        }
        var first = 0;
        while (tasks.Length - first > MaxTasks || total > MaxTotalBytes)
        {
            if (first >= tasks.Length)
            {
                break;
            }
            try
            {
                tasks[first].Delete(recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                break; // Keep the rest rather than failing retention.
            }
            total -= sizes[first];
            first++;
        }
    }

    private static long? Measure(string directory)
    {
        try
        {
            long total = 0;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                foreach (var entry in Directory.GetFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else total += new FileInfo(entry).Length;
                    if (total > MaxTotalBytes) return total;
                }
            }
            return total;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
