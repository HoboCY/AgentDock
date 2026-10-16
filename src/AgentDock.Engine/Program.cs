using System.Diagnostics;
using System.Text.Json;

namespace AgentDock.Engine;

/// <summary>
/// In-sandbox launcher: <c>AgentDock.Engine --launch &lt;launch.json&gt;</c> applies
/// the isolated harness environment, records the current logon SID for the
/// pinned Bun stdio adapter, then runs the prepared OpenCode command with
/// inherited stdio and returns its exit code. It performs no other action and
/// never touches the real credential (the launch file contains only the
/// per-job relay capability). The host references this assembly as a library;
/// this entry point exists for running the same binary inside the sandbox.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--launch", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: AgentDock.Engine --launch <launch.json>");
            return 2;
        }
        try
        {
            return Run(args[1]);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("AgentDock.Engine launch failed: " + error.Message);
            return 3;
        }
    }

    private static int Run(string launchPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(launchPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("command", out var commandElement) ||
            commandElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The launch file has no command array.");
        }
        var command = commandElement.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToArray();
        if (command.Length == 0)
        {
            throw new InvalidDataException("The launch command is empty.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        for (var index = 1; index < command.Length; index++)
        {
            startInfo.ArgumentList.Add(command[index]);
        }
        startInfo.Environment.Clear();
        if (root.TryGetProperty("env", out var environmentElement) && environmentElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in environmentElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    startInfo.Environment[property.Name] = property.Value.GetString()!;
                }
            }
        }
        var logonSid = WindowsToken.TryGetLogonSid();
        if (logonSid is not null)
        {
            startInfo.Environment["OPENCODE_PIPE_LOGON_SID"] = logonSid;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The prepared OpenCode command could not be started.");
        var input = Task.Run(() => Pump(Console.OpenStandardInput(), process.StandardInput.BaseStream, closeTarget: true));
        var output = Task.Run(() => Pump(process.StandardOutput.BaseStream, Console.OpenStandardOutput(), closeTarget: false));
        var error = Task.Run(() => Pump(process.StandardError.BaseStream, Console.OpenStandardError(), closeTarget: false));
        process.WaitForExit();
        Task.WaitAll(output, error);
        _ = input;
        return process.ExitCode;
    }

    private static void Pump(Stream source, Stream target, bool closeTarget)
    {
        try
        {
            source.CopyTo(target, 64 * 1024);
            target.Flush();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The far side closed its pipe first; nothing else to move.
        }
        finally
        {
            if (closeTarget)
            {
                try
                {
                    target.Dispose();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // Already closed.
                }
            }
        }
    }
}
