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
    private readonly Func<string?> _findExecutable;
    private string? _executable;
    public string? Executable
    {
        get => _executable;
        set => _executable = ResolveExecutable(value) ?? value;
    }

    public CodexIntegration(string dataHome, string? codexHome = null, string? executable = null, Func<string?>? findExecutable = null)
    {
        _findExecutable = findExecutable ?? FindExecutable;
        CodexHome = Path.GetFullPath(codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        Marketplace = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataHome))!, "plugin");
        Executable = executable ?? _findExecutable();
    }

    public async Task<CodexConnection> InspectAsync(CancellationToken ct)
    {
        RefreshExecutable();
        if (Executable is null || !File.Exists(Executable))
            return new(Executable, false, Executable is { } path && Directory.Exists(path) && IsApplicationBundle(path)
                ? "所选应用包中未找到 Codex 命令程序。请选择包含 Codex 的 ChatGPT/Codex 应用，或直接选择 codex 命令文件。"
                : "未找到 Codex 命令程序。请选择已安装的 ChatGPT/Codex 应用，或直接选择 codex 命令文件。");
        var json = await RunAsync(["plugin", "list", "--json"], ct);
        return ParseStatus(json, Executable, Marketplace);
    }

    public async Task<CodexConnection> RegisterAsync(CancellationToken ct)
    {
        RefreshExecutable();
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

    internal ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Executable!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = CodexHome
        };
        start.Environment["CODEX_HOME"] = CodexHome;
        // npm's Unix launcher uses /usr/bin/env node. Finder-launched apps may lack the nvm PATH.
        // Keep the selected bin/symlink location so its matching Node is used only by this child.
        var bin = Path.GetDirectoryName(Path.GetFullPath(Executable!))!;
        if (File.Exists(Path.Combine(bin, "node")))
        {
            start.Environment.TryGetValue("PATH", out var inheritedPath);
            start.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath)
                ? bin : bin + Path.PathSeparator + inheritedPath;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private async Task<string> RunAsync(string[] arguments, CancellationToken ct)
    {
        if (Executable is null || !File.Exists(Executable)) throw new InvalidOperationException("请先选择 Codex 程序文件。");
        Directory.CreateDirectory(CodexHome);
        var start = CreateStartInfo(arguments);
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
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return FindUnixExecutable(userHome, Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("NVM_DIR"), ["/Applications", Path.Combine(userHome, "Applications")],
                ["/opt/homebrew/bin", "/usr/local/bin"]);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static string? FindUnixExecutable(string userHome, string? searchPath, string? nvmDirectory,
        string[] applicationDirectories, string[] systemBinDirectories)
    {
        var candidates = (searchPath ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.Combine(p.Trim('"'), "codex")).ToList();
        var bundled = FindBundledExecutable(applicationDirectories);
        if (bundled is not null) candidates.Add(bundled);
        candidates.Add(Path.Combine(userHome, ".local", "bin", "codex"));
        candidates.AddRange(systemBinDirectories.Select(directory => Path.Combine(directory, "codex")));
        return candidates.FirstOrDefault(File.Exists) ?? FindNvmExecutable(string.IsNullOrWhiteSpace(nvmDirectory)
            ? Path.Combine(userHome, ".nvm") : nvmDirectory);
    }

    private static string? FindNvmExecutable(string nvmDirectory)
    {
        try
        {
            var versions = Path.Combine(nvmDirectory, "versions", "node");
            if (!Directory.Exists(versions)) return null;
            // No shell startup files or recursive home scan. Prefer the newest installed version with Codex.
            return Directory.GetDirectories(versions, "v*")
                .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path).AsSpan(1), out var version) ? version : null))
                .Where(candidate => candidate.Version is not null)
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => Path.Combine(candidate.Path, "bin", "codex"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null; // An inaccessible optional install must not prevent manual selection.
        }
    }

    /// <summary>Recover from installation, app renames and invalid selections while retaining a useful diagnostic.</summary>
    public string? RefreshExecutable() => Executable = ResolveExecutable(Executable) ?? _findExecutable() ?? Executable;

    /// <summary>Resolve an app selection to its CLI; never execute the GUI entry point as a CLI.</summary>
    public static string? ResolveExecutable(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) return null;
        if (File.Exists(selectedPath)) return selectedPath;
        if (!Directory.Exists(selectedPath) || !IsApplicationBundle(selectedPath)) return null;
        return new[]
        {
            Path.Combine(selectedPath, "Contents", "Resources", "codex"),
            Path.Combine(selectedPath, "Contents", "MacOS", "codex"),
        }.FirstOrDefault(File.Exists);
    }

    public static string? FindBundledExecutable(params string[] applicationDirectories)
    {
        foreach (var application in new[] { "ChatGPT.app", "Codex.app" })
            foreach (var directory in applicationDirectories)
                if (ResolveExecutable(Path.Combine(directory, application)) is { } executable) return executable;
        return null;
    }

    private static bool IsApplicationBundle(string path) =>
        Path.TrimEndingDirectorySeparator(path).EndsWith(".app", StringComparison.OrdinalIgnoreCase);
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
