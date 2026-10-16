using System.Security.Cryptography;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// Bounded before/after SHA-256 snapshot of the task project. The observed
/// difference is evidence of what the project looked like around the task; it is
/// not proof of who changed a file, because other processes may write to the
/// same project while the task runs.
///
/// Bounded by design: a fixed ignore list for generated/dependency directories,
/// a maximum file count, a maximum single-file size and a maximum total hashed
/// bytes. Reparse points (junctions/symlinks) are never followed, so entries
/// outside the project root cannot enter the hash set and directory cycles
/// cannot occur. Ignored or skipped files are outside the observation.
/// </summary>
internal static class ProjectSnapshot
{
    public const int MaxFiles = 20_000;
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const long MaxTotalBytes = 1024L * 1024 * 1024;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".venv", "venv", "target", "dist",
        "build", "__pycache__", ".idea", ".vs", ".nuget", "packages", ".next", ".gradle", ".cache",
    };

    public static Dictionary<string, string> Capture(string projectDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(projectDirectory))
        {
            return result;
        }
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(projectDirectory));
        while (pending.Count > 0 && result.Count < MaxFiles && totalBytes < MaxTotalBytes)
        {
            var directory = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (result.Count >= MaxFiles || totalBytes >= MaxTotalBytes)
                {
                    break;
                }
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    continue;
                }
                // Reparse points are skipped before recursion: a junction or
                // symlink could escape the project root or form a cycle.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(entry)))
                    {
                        pending.Push(entry);
                    }
                    continue;
                }
                long length;
                try
                {
                    length = new FileInfo(entry).Length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    continue;
                }
                if (length > MaxFileBytes)
                {
                    continue;
                }
                var hash = TryHash(entry);
                if (hash is null)
                {
                    continue;
                }
                totalBytes += length;
                var relative = Path.GetRelativePath(projectDirectory, entry).Replace('\\', '/');
                result[relative] = hash;
            }
        }
        return result;
    }

    private static string? TryHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Differences as project-relative paths ('/' separators). A missing side is
    /// null: before-null means created, after-null means deleted.
    /// </summary>
    public static FileChange[] Diff(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var changes = new List<FileChange>();
        foreach (var (path, beforeHash) in before)
        {
            if (!after.TryGetValue(path, out var afterHash) ||
                !string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new FileChange(path, beforeHash, afterHash));
            }
        }
        foreach (var (path, afterHash) in after)
        {
            if (!before.ContainsKey(path))
            {
                changes.Add(new FileChange(path, null, afterHash));
            }
        }
        changes.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return [.. changes];
    }
}
