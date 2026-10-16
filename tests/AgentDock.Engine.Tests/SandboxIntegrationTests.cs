using System.Diagnostics;
using System.Text.Json;
using AgentDock.Core;
using Xunit;

namespace AgentDock.Engine.Tests;

/// <summary>
/// Opt-in real-sandbox probes. Enable with AGENTDOCK_ENGINE_SANDBOX_TESTS=1 and
/// point AGENTDOCK_TEST_OPENCODE / AGENTDOCK_TEST_SANDBOX / AGENTDOCK_TEST_SHELL
/// at the installed pinned executables. The upstream model is a local scripted
/// handler; no real key and no remote request are used.
/// </summary>
public sealed class SandboxFactAttribute : FactAttribute
{
    public SandboxFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Windows sandbox fixtures; MacExecutionTests cover macOS."; return; }
        if (Environment.GetEnvironmentVariable("AGENTDOCK_ENGINE_SANDBOX_TESTS") != "1")
        {
            Skip = "Set AGENTDOCK_ENGINE_SANDBOX_TESTS=1 with the pinned tool paths to run real-sandbox probes.";
        }
        else if (Environment.GetEnvironmentVariable("AGENTDOCK_TEST_OPENCODE") is null ||
                 Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX") is null ||
                 Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SHELL") is null)
        {
            Skip = "Set AGENTDOCK_TEST_OPENCODE, AGENTDOCK_TEST_SANDBOX and AGENTDOCK_TEST_SHELL to the pinned tools.";
        }
    }
}

public sealed class SandboxIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdock-sandbox-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _project;
    private readonly string _neighbor;

    public SandboxIntegrationTests()
    {
        _home = Path.Combine(_root, "home");
        _project = Path.Combine(_root, "project");
        _neighbor = Path.Combine(_root, "neighbor");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_neighbor);
        File.WriteAllText(Path.Combine(_neighbor, "private-fixture.txt"), "unrelated fixture");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Fixture cleanup is best effort.
        }
    }

    private DockConfiguration Settings() => new()
    {
        EnginePath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_OPENCODE")!,
        SandboxPath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SANDBOX")!,
        ShellPath = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_SHELL")!,
    };

    private ExecutionTask TaskFor(string role, string prompt) =>
        new("sandbox-task", _project, role, prompt, Settings().Roles[role], Settings());

    private OpenCodeEngine Engine(ScriptedModel model) =>
        new(_home, () => "fixture-not-a-real-key", new EngineHooks { RelayTransport = new ScriptedHandler(model),
            RunnerExecutable = Environment.GetEnvironmentVariable("AGENTDOCK_TEST_RUNNER") });

    [SandboxFact]
    public async Task ReadOnlyProbeCannotTouchProjectOrNeighbor()
    {
        var neighborFile = Path.Combine(_neighbor, "forbidden.txt");
        var projectFile = Path.Combine(_project, "forbidden.txt");
        var script = "$r=@{};" +
                     $"try{{Set-Content -LiteralPath '{neighborFile}' -Value x -ErrorAction Stop;$r.neighbor=$true}}catch{{$r.neighbor=$false}};" +
                     $"try{{Set-Content -LiteralPath '{projectFile}' -Value x -ErrorAction Stop;$r.project=$true}}catch{{$r.project=$false}};" +
                     "try{New-Item -ItemType File -Force -Path (Join-Path $env:TEMP 'allowed.txt') -ErrorAction Stop | Out-Null;$r.runtime=$true}catch{$r.runtime=$false};" +
                     "$r.inheritedKey=[bool]($env:DEEPSEEK_API_KEY -or $env:OPENAI_API_KEY);" +
                     "$r | ConvertTo-Json -Compress";
        var model = new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = script, description = "Check fixture permissions" }),
            ScriptedModel.Text("FIXTURE_OK"));
        var events = new List<EngineEvent>();
        var result = await Engine(model).ExecuteAsync(TaskFor("explorer", "Probe read-only permissions."),
            events.Add, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var output = string.Join("\n", events.Where(item => item.Kind == "tool").Select(item => item.Text));
        using var probe = JsonDocument.Parse(ExtractJsonObject(output));
        Assert.False(probe.RootElement.GetProperty("neighbor").GetBoolean());
        Assert.False(probe.RootElement.GetProperty("project").GetBoolean());
        Assert.True(probe.RootElement.GetProperty("runtime").GetBoolean());
        Assert.False(probe.RootElement.GetProperty("inheritedKey").GetBoolean());
        Assert.False(File.Exists(neighborFile));
        Assert.False(File.Exists(projectFile));
        Assert.Empty(result.Changes ?? []);
        Assert.Contains(events, item => item.Kind == "usage" && item.InputTokens is > 0);
    }

    [SandboxFact]
    public async Task VerifierBuildsReadOnlySourceWithAllArtifactsRedirected()
    {
        File.WriteAllText(Path.Combine(_project, "Check.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(_project, "Program.cs"), "System.Console.WriteLine(\"CHECK_OK\");");
        var nuget = Path.Combine(_project, "NuGet.Config");
        File.WriteAllText(nuget, "<configuration><packageSources><clear /></packageSources></configuration>");
        var script = "$a = Join-Path $env:USERPROFILE '..\\check-artifacts';" +
            $"& dotnet build '{Path.Combine(_project, "Check.csproj")}' --artifacts-path $a -p:RestoreConfigFile='{nuget}' --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false --nologo -v:q;" +
            "\"BUILD_EXIT=$LASTEXITCODE\"; $exe=Get-ChildItem -LiteralPath (Join-Path $a 'bin') -Filter Check.dll -Recurse | Select-Object -First 1; & dotnet $exe.FullName; \"CHECK_EXIT=$LASTEXITCODE\"";
        var events = new List<EngineEvent>();
        var result = await Engine(new ScriptedModel(ScriptedModel.ToolCall("bash", new { command = script, description = "Build readonly fixture", timeout = 120000 }),
            ScriptedModel.Text("FIXTURE_OK"))).ExecuteAsync(TaskFor("verifier", "Compile and run the check fixture, preserving source."), events.Add, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        var output = string.Join("\n", events.Where(e => e.Kind == "tool").Select(e => e.Text));
        Assert.True(output.Contains("BUILD_EXIT=0"), output);
        Assert.Contains("CHECK_OK", output);
        Assert.Contains("CHECK_EXIT=0", output);
        Assert.Empty(result.Changes!);
        Assert.False(Directory.Exists(Path.Combine(_project, "obj")));
        Assert.False(Directory.Exists(Path.Combine(_project, "bin")));
    }

    [SandboxFact]
    public async Task VerifierCanProduceArtifactsAndRedirectDotnetOutput()
    {
        var script = "$a = Join-Path $env:USERPROFILE '..\\check-artifacts';" +
                     "New-Item -ItemType Directory -Force -Path $a | Out-Null;" +
                     "& dotnet --info > (Join-Path $a 'dotnet-info.txt') 2>&1; $code=$LASTEXITCODE;" +
                     "if(Test-Path (Join-Path $a 'dotnet-info.txt')){'ARTIFACT_OK'}else{'ARTIFACT_MISSING'};" +
                     "\"DOTNET_EXIT=$code\"";
        var model = new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = script, description = "Produce check artifacts" }),
            ScriptedModel.Text("FIXTURE_OK"));
        var events = new List<EngineEvent>();
        var result = await Engine(model).ExecuteAsync(TaskFor("verifier", "Run checks and produce artifacts."),
            events.Add, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var output = string.Join("\n", events.Where(item => item.Kind == "tool").Select(item => item.Text));
        Assert.Contains("ARTIFACT_OK", output);
        Assert.Contains("DOTNET_EXIT=0", output);
        Assert.Empty(result.Changes ?? []);
    }

    [SandboxFact]
    public async Task WriterEditIsObservedWithHashesAndDisclaimer()
    {
        var receipt = Path.Combine(_project, "receipt.txt");
        var model = new ScriptedModel(
            ScriptedModel.ToolCall("write", new { filePath = receipt, content = "FIXTURE_RECEIPT\n" }),
            ScriptedModel.Text("FIXTURE_OK"));
        var result = await Engine(model).ExecuteAsync(TaskFor("worker", "Write the receipt fixture."),
            _ => { }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("FIXTURE_RECEIPT\n", File.ReadAllText(receipt));
        var change = Assert.Single(result.Changes!, item => item.Path == "receipt.txt");
        Assert.Null(change.BeforeSha256);
        Assert.NotNull(change.AfterSha256);
        Assert.Contains("not attribution", result.Result);
    }

    [SandboxFact]
    public async Task CancellationKillsTheToolChildAndRetainsProjectEdits()
    {
        var pidFile = Path.Combine(_project, "child.pid");
        var script = $"Set-Content -LiteralPath '{pidFile}' -Value $PID;" +
                     "Start-Sleep -Seconds 90";
        var model = new ScriptedModel(
            ScriptedModel.ToolCall("bash", new { command = script, description = "Cancellation fixture", timeout = 120000 }),
            ScriptedModel.Text("FIXTURE_OK"));
        using var source = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline && !source.IsCancellationRequested)
            {
                await Task.Delay(200);
            }
            if (File.Exists(pidFile))
            {
                source.Cancel();
            }
        });
        var result = await Engine(model).ExecuteAsync(TaskFor("worker", "Start the cancellation fixture."),
            _ => { }, source.Token);
        await watcher;

        Assert.False(result.Success);
        Assert.Contains("Cancelled", result.Error);
        Assert.True(File.Exists(pidFile), "the script process should have written its PID before cancellation");
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (IsRunning(pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }
        Assert.False(IsRunning(pid), "the sandboxed tool child survived cancellation");
        Assert.Contains(result.Changes!, change => change.Path == "child.pid");
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

    private static string ExtractJsonObject(string text)
    {
        // Tool events also contain the PowerShell command (which has braces).
        // This probe emits one final flat JSON object after that command.
        var start = text.LastIndexOf('{');
        var end = text.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, "probe output did not contain a JSON object: " + text);
        return text[start..(end + 1)];
    }
}
