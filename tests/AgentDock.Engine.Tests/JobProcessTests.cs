using System.Diagnostics;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class JobProcessTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "agentdock-job-" + Guid.NewGuid().ToString("N"));
    private readonly string _cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public JobProcessTests()
    {
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Dictionary<string, string> ParentEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry pair in System.Environment.GetEnvironmentVariables())
        {
            if (pair.Value is string value)
            {
                environment[(string)pair.Key] = value;
            }
        }
        return environment;
    }

    private static string[] ReadAll(JobProcess process)
    {
        var lines = new List<string>();
        foreach (var line in JobProcess.ReadLines(process.Stdout))
        {
            lines.Add(line);
        }
        return [.. lines];
    }

    [WindowsJobFact]
    public void MissingProjectIsNotRecreated()
    {
        var missing = Path.Combine(_work, "removed-project");
        Assert.Throws<DirectoryNotFoundException>(() => JobProcess.Start(_cmd, ["/c", "echo should-not-run"], missing, ParentEnvironment()));
        Assert.False(Directory.Exists(missing));
    }

    [WindowsJobFact]
    public void RunsCommandWithPipesAndPreservesExitCode()
    {
        using var process = JobProcess.Start(_cmd, ["/c", "echo hello-job & exit /b 7"], _work, ParentEnvironment());
        var lines = ReadAll(process);
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(7, process.ExitCode);
        Assert.Contains(lines, line => line.Contains("hello-job", StringComparison.Ordinal));
    }

    [WindowsJobFact]
    public void StandardInputReachesTheChild()
    {
        using var process = JobProcess.Start(_cmd, ["/c", "findstr ."], _work, ParentEnvironment());
        var bytes = System.Text.Encoding.UTF8.GetBytes("stdin-payload\n");
        process.Stdin.Write(bytes);
        process.Stdin.Dispose();
        var lines = ReadAll(process);
        Assert.True(process.WaitForExit(30_000));
        Assert.Contains(lines, line => line.Contains("stdin-payload", StringComparison.Ordinal));
    }

    [WindowsJobFact]
    public void TerminateKillsTheWholeProcessTreeIncludingLateChildren()
    {
        var pwsh = FindPwsh();
        if (pwsh is null) { return; } // pwsh is required for the descendant probe
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var pidFile = Path.Combine(_work, "child.pid");
        var script = $"$p = Start-Process -FilePath '{ping}' -ArgumentList '-n','120','127.0.0.1' " +
                     $"-PassThru -WindowStyle Hidden; $p.Id | Out-File -Encoding ascii '{pidFile}.tmp'; " +
                     $"Move-Item -LiteralPath '{pidFile}.tmp' -Destination '{pidFile}'; Start-Sleep -Seconds 120";
        using var process = JobProcess.Start(pwsh!, ["-NoProfile", "-NonInteractive", "-Command", script], _work,
            ParentEnvironment());
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
        {
            Assert.False(process.HasExited, "the helper exited before spawning its child");
            Thread.Sleep(100);
        }
        Assert.True(File.Exists(pidFile), "the descendant PID file was not written");
        var childPid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(IsRunning(childPid), "the late child should be running before termination");

        process.Terminate();
        Assert.True(process.WaitForExit(15_000), "the job process did not exit after termination");

        var childDeadline = DateTime.UtcNow.AddSeconds(10);
        while (IsRunning(childPid) && DateTime.UtcNow < childDeadline)
        {
            Thread.Sleep(100);
        }
        Assert.False(IsRunning(childPid), "the late child survived job termination");
    }

    [WindowsJobFact]
    public void DisposingWithoutTerminateStillKillsTheTree()
    {
        var pwsh = FindPwsh();
        if (pwsh is null) { return; } // pwsh is required for the descendant probe
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var pidFile = Path.Combine(_work, "child2.pid");
        var script = $"$p = Start-Process -FilePath '{ping}' -ArgumentList '-n','120','127.0.0.1' " +
                     $"-PassThru -WindowStyle Hidden; $p.Id | Out-File -Encoding ascii '{pidFile}.tmp'; " +
                     $"Move-Item -LiteralPath '{pidFile}.tmp' -Destination '{pidFile}'; Start-Sleep -Seconds 120";
        using var process = JobProcess.Start(pwsh!, ["-NoProfile", "-NonInteractive", "-Command", script], _work,
            ParentEnvironment());
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
        Assert.True(File.Exists(pidFile));
        var childPid = int.Parse(File.ReadAllText(pidFile).Trim());
        process.Dispose();

        var childDeadline = DateTime.UtcNow.AddSeconds(10);
        while (IsRunning(childPid) && DateTime.UtcNow < childDeadline)
        {
            Thread.Sleep(100);
        }
        Assert.False(IsRunning(childPid), "kill-on-job-close did not terminate the late child");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? FindPwsh()
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }
}

public sealed class WindowsJobFactAttribute : FactAttribute
{
    public WindowsJobFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires Windows Job Objects."; }
}
