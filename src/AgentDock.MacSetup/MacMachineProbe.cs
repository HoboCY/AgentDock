using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>
/// 只读本机环境探测（install/machine.json）：系统 shell（/bin/bash，OS 自带）、内置 Git、Codex CLI、
/// 旧 deepseek_agents 入口；不读取凭据、设置内容或任何账户数据。
/// </summary>
internal static class MacMachineProbe
{
    public static MachineInfo Run(string versionDir, List<string> warnings)
    {
        const string shellPath = "/bin/bash";
        var shellVersion = RunCapture(shellPath, "--version")?.Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(shellVersion))
        {
            warnings.Add("无法运行 /bin/bash --version；执行引擎依赖系统自带 /bin/bash。");
        }
        var shellSha = TrySha256(shellPath);
        if (!File.Exists(shellPath))
        {
            warnings.Add("系统缺少 /bin/bash，执行引擎无法运行命令。");
        }

        var bundledGit = Path.Combine(versionDir, "engines", "git", "bin", "git");
        if (!File.Exists(bundledGit))
        {
            warnings.Add("安装包缺少内置 Git（engines/git/bin/git）；任务里的 git 命令将不可用，请使用完整安装包重新安装。");
        }

        var codexCli = FindOnPath("codex");
        var legacyDetails = DetectLegacyDeepSeekAgents();
        if (legacyDetails.Count > 0)
        {
            warnings.Add("检测到旧 deepseek_agents 入口，可能与 AgentDock 重复派发任务；AgentDock 不迁移、不改动旧配置，请自行决定是否停用旧入口。");
        }

        return new MachineInfo(
            Schema: 1,
            ShellPath: shellPath,
            ShellVersion: shellVersion,
            ShellSource: "system",
            ShellSha256: shellSha,
            CodexCli: codexCli,
            LegacyDeepSeekAgents: legacyDetails.Count > 0,
            LegacyDetails: legacyDetails.ToArray(),
            Warnings: warnings.Distinct(StringComparer.Ordinal).ToArray(),
            ProbedAt: DateTimeOffset.UtcNow.ToString("o"),
            OsDescription: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
    }

    internal static List<string> DetectLegacyDeepSeekAgents()
    {
        var details = new List<string>();
        string codexHome;
        try
        {
            codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
        catch
        {
            return details;
        }

        try
        {
            var config = Path.Combine(codexHome, "config.toml");
            if (File.Exists(config))
            {
                var text = File.ReadAllText(config);
                if (text.Contains("deepseek-agents", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("deepseek_agents", StringComparison.OrdinalIgnoreCase))
                {
                    details.Add($"Codex 配置仍包含 deepseek-agents 相关条目：{config}");
                }
            }
        }
        catch
        {
            details.Add("Codex 配置存在但无法读取（只读检测被拒绝）。");
        }

        foreach (var relative in new[] { Path.Combine("mcp", "deepseek-agents"), "deepseek-agents" })
        {
            var dir = Path.Combine(codexHome, relative);
            if (Directory.Exists(dir)) details.Add($"存在旧 deepseek_agents 目录：{dir}");
        }

        return details;
    }

    internal static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 非法 PATH 段直接跳过。
            }
        }
        return null;
    }

    private static string? TrySha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static string? RunCapture(string executable, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(arguments);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }
}
