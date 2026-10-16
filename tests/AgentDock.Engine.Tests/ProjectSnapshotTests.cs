using System.Diagnostics;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class ProjectSnapshotTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "agentdock-snap-" + Guid.NewGuid().ToString("N"));

    public ProjectSnapshotTests()
    {
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_project, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void DetectsCreatedModifiedAndDeletedFilesWithHashes()
    {
        Write("keep.txt", "same");
        Write("modified.txt", "before");
        Write("deleted.txt", "gone");
        var before = ProjectSnapshot.Capture(_project);

        Write("modified.txt", "after");
        File.Delete(Path.Combine(_project, "deleted.txt"));
        Write("created/new.txt", "fresh");
        var after = ProjectSnapshot.Capture(_project);
        var changes = ProjectSnapshot.Diff(before, after);

        var created = Assert.Single(changes, change => change.Path == "created/new.txt");
        Assert.Null(created.BeforeSha256);
        Assert.NotNull(created.AfterSha256);

        var modified = Assert.Single(changes, change => change.Path == "modified.txt");
        Assert.NotNull(modified.BeforeSha256);
        Assert.NotNull(modified.AfterSha256);
        Assert.NotEqual(modified.BeforeSha256, modified.AfterSha256);

        var deleted = Assert.Single(changes, change => change.Path == "deleted.txt");
        Assert.NotNull(deleted.BeforeSha256);
        Assert.Null(deleted.AfterSha256);

        Assert.DoesNotContain(changes, change => change.Path == "keep.txt");
    }

    [Fact]
    public void IdenticalSnapshotsProduceNoChanges()
    {
        Write("a.txt", "x");
        var before = ProjectSnapshot.Capture(_project);
        var after = ProjectSnapshot.Capture(_project);
        Assert.Empty(ProjectSnapshot.Diff(before, after));
    }

    [Fact]
    public void GeneratedAndDependencyDirectoriesAreIgnored()
    {
        Write("src/main.cs", "code");
        Write("node_modules/pkg/index.js", "module");
        Write("bin/output.dll", "binary");
        Write("obj/temp.bin", "temp");
        var snapshot = ProjectSnapshot.Capture(_project);
        Assert.Contains("src/main.cs", snapshot.Keys);
        Assert.DoesNotContain(snapshot.Keys, path => path.StartsWith("node_modules/", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Keys, path => path.StartsWith("bin/", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Keys, path => path.StartsWith("obj/", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureOfMissingDirectoryIsEmpty()
    {
        Assert.Empty(ProjectSnapshot.Capture(Path.Combine(_project, "does-not-exist")));
    }

    [Fact]
    public void ReparsePointDirectoriesAreNotFollowedOrHashed()
    {
        var outside = Path.Combine(Path.GetTempPath(), "agentdock-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "outside");
        Write("inside/keep.txt", "inside");
        var link = Path.Combine(_project, "link");
        if (!TryCreateJunction(link, outside))
        {
            Directory.Delete(outside, recursive: true);
            return; // Creating a reparse point is environment-dependent.
        }
        try
        {
            var snapshot = ProjectSnapshot.Capture(_project);
            Assert.Contains("inside/keep.txt", snapshot.Keys);
            Assert.DoesNotContain(snapshot.Keys, path => path.StartsWith("link", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snapshot.Keys, path => path.Contains("outside", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteLink(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void JunctionCyclesDoNotRecurseAndStayInsideTheRoot()
    {
        Write("sub/keep.txt", "x");
        var link = Path.Combine(_project, "sub", "loop");
        if (!TryCreateJunction(link, _project))
        {
            return; // Creating a reparse point is environment-dependent.
        }
        try
        {
            var snapshot = ProjectSnapshot.Capture(_project);
            Assert.Contains("sub/keep.txt", snapshot.Keys);
            Assert.DoesNotContain(snapshot.Keys, path => path.Contains("loop", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteLink(link);
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Fall through to an unelevated junction.
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void DeleteLink(string link)
    {
        try
        {
            Directory.Delete(link, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort; the fixture directory cleanup does not follow links.
        }
    }
}
