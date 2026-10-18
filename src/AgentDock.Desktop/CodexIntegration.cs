using System.Diagnostics;
using System.Text.Json;
using AgentDock.Core;

namespace AgentDock.Desktop;

public sealed record CodexConnection(string? Executable, bool Registered, string Message);

/// <summary>Uses the supported plugin CLI; never rewrites the user's TOML or authentication.</summary>
public sealed class CodexIntegration
{
    public string CodexHome { get; }
    public string Marketplace { get; }
    public string? Executable { get; set; }

    public CodexIntegration(string dataHome, string? codexHome = null, string? executable = null)
    {
        CodexHome = Path.GetFullPath(codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        Marketplace = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataHome))!, "plugin");
        Executable = executable ?? FindExecutable();
    }

    public async Task<CodexConnection> InspectAsync(CancellationToken ct)
    {
        if (Executable is null || !File.Exists(Executable))
            return new(Executable, false, "未找到 Codex。请先安装 Codex，或选择已安装的程序文件。");
        var json = await RunAsync(["plugin", "list", "--json"], ct);
        return ParseStatus(json, Executable, Marketplace);
    }

    public async Task<CodexConnection> RegisterAsync(CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(Marketplace, ".agents", "plugins", "marketplace.json")))
            throw new InvalidOperationException("安装目录缺少插件源，请使用完整安装包修复 AgentDock。");
        await RunAsync(["plugin", "marketplace", "add", Marketplace, "--json"], ct);
        await RunAsync(["plugin", "add", "agentdock@agentdock-local", "--json"], ct);
        var state = await InspectAsync(ct);
        if (!state.Registered) throw new InvalidOperationException(state.Message);
        return state;
    }

    public static CodexConnection ParseStatus(string json, string executable, string marketplace)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Codex 返回了无法识别的插件状态，请更新 Codex 后重试。");
        foreach (var item in installed.EnumerateArray())
        {
            if (!item.TryGetProperty("pluginId", out var id) || id.GetString() != "agentdock@agentdock-local") continue;
            var enabled = item.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            var present = item.TryGetProperty("installed", out var p) && p.ValueKind == JsonValueKind.True;
            var source = item.TryGetProperty("marketplaceSource", out var s) && s.TryGetProperty("source", out var path)
                ? path.GetString() : null;
            var matches = source is not null && SamePath(source, marketplace);
            return new(executable, enabled && present && matches, enabled && present && matches
                ? "插件已注册并启用。重启 Codex 后，在新对话中使用。"
                : !matches ? "插件指向其他安装位置，请重新接入当前 AgentDock。" : "插件尚未启用，请点击接入 Codex。");
        }
        return new(executable, false, "已找到 Codex，尚未接入 AgentDock 插件。");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.StartsWith(@"\\?\", StringComparison.Ordinal) ? left[4..] : left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<string> RunAsync(string[] arguments, CancellationToken ct)
    {
        if (Executable is null || !File.Exists(Executable)) throw new InvalidOperationException("请先选择 Codex 程序文件。");
        Directory.CreateDirectory(CodexHome);
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = CodexHome
        };
        start.Environment["CODEX_HOME"] = CodexHome;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            _ = await stderr; // Config diagnostics may contain private values; never display raw output.
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Codex 插件操作失败（退出码 {process.ExitCode}）。请检查 Codex 的插件列表，修复失效的插件源路径后重试。");
            return output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            throw new InvalidOperationException("Codex 操作已取消或超时。请刷新状态后再继续。");
        }
    }

    public static string? FindExecutable()
    {
        var file = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), file)).ToList();
        if (OperatingSystem.IsWindows())
        {
            var appBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(appBin))
                candidates.AddRange(Directory.GetDirectories(appBin).OrderByDescending(Directory.GetLastWriteTimeUtc).Select(p => Path.Combine(p, file)));
        }
        else
        {
            candidates.AddRange(["/Applications/Codex.app/Contents/Resources/codex", "/opt/homebrew/bin/codex", "/usr/local/bin/codex"]);
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class OnboardingProgress
{
    public bool Completed { get; set; }

    public static bool IsComplete(string dataHome)
    {
        try { return AtomicJson.Read<OnboardingProgress>(Path.Combine(dataHome, "onboarding.json")).Completed; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    public static void Complete(string dataHome) => AtomicJson.Write(Path.Combine(dataHome, "onboarding.json"), new OnboardingProgress { Completed = true });
}
