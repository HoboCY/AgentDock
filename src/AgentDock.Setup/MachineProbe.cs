using System.Diagnostics;
using Microsoft.Win32;

namespace AgentDock.Setup;

/// <summary>
/// 只读本机环境探测：内置 Git（MinGit）优先、系统 Git Bash 回退、Codex CLI、旧 deepseek_agents 入口。
/// 不读取凭据与设置内容。
/// </summary>
internal static class MachineProbe
{
    public static MachineInfo Run(InstallLayout layout, string? versionDir, IEnumerable<string>? extraWarnings = null)
    {
        var warnings = new List<string>();
        if (extraWarnings is not null) warnings.AddRange(extraWarnings);

        var (shell, shellVersion, shellSource, shellSha256) = DetectShell(versionDir);
        if (shell is null)
        {
            warnings.Add("安装包缺少内置 Git（engines/git/usr/bin/sh.exe），且系统未检测到 Git Bash；执行引擎将无法运行命令。" +
                "请使用包含 MinGit 的完整安装包重新安装（干净系统无需预装 Git）。");
        }
        else if (shellSource == "system")
        {
            warnings.Add("安装包缺少内置 Git（engines/git/usr/bin/sh.exe），已回退到系统 Git Bash；请使用完整安装包重新安装。");
        }

        var codexCli = FindOnPath("codex.exe") ?? FindOnPath("codex.cmd");
        var legacyDetails = DetectLegacyDeepSeekAgents();
        if (legacyDetails.Count > 0)
        {
            warnings.Add("检测到旧 deepseek_agents 入口，可能与 AgentDock 重复派发任务；AgentDock 不迁移、不改动旧配置，请自行决定是否停用旧入口。");
        }

        return new MachineInfo(
            Schema: 1,
            ShellPath: shell,
            ShellVersion: shellVersion,
            ShellSource: shellSource,
            ShellSha256: shellSha256,
            CodexCli: codexCli,
            LegacyDeepSeekAgents: legacyDetails.Count > 0,
            LegacyDetails: legacyDetails.ToArray(),
            Warnings: warnings.Distinct(StringComparer.Ordinal).ToArray(),
            ProbedAt: DateTimeOffset.UtcNow.ToString("o"),
            OsDescription: Environment.OSVersion.VersionString,
            Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
    }

    /// <summary>内置 Git 优先（Host PackageDefaults 使用 engines/git/usr/bin/sh.exe）；缺失时才回退系统 Git Bash。</summary>
    internal static (string? Shell, string? Version, string? Source, string? Sha256) DetectShell(string? versionDir)
    {
        if (versionDir is { Length: > 0 })
        {
            var bundled = Path.Combine(versionDir, "engines", "git", "usr", "bin", "sh.exe");
            if (File.Exists(bundled))
            {
                var full = Path.GetFullPath(bundled);
                var version = RunCapture(full, "--version")?.Split('\n')[0].Trim();
                return (full, version, "bundled", TrySha256(full));
            }
        }
        var (bash, bashVersion) = DetectGitBash();
        return bash is null ? (null, null, null, null) : (bash, bashVersion, "system", null);
    }

    private static string? TrySha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    internal static (string? Bash, string? Version) DetectGitBash()
    {
        var roots = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GitForWindows");
            if (key?.GetValue("InstallPath") is string installPath && installPath.Length > 0) roots.Add(installPath);
        }
        catch
        {
            // 只读探测失败不影响其它候选路径。
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"));
        var onPath = FindOnPath("git.exe");
        if (onPath is not null)
        {
            roots.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(onPath)!, "..")));
        }

        foreach (var root in roots)
        {
            var bash = Path.Combine(root, "bin", "bash.exe");
            if (File.Exists(bash))
            {
                var version = RunCapture(bash, "--version")?.Split('\n')[0].Trim();
                if (string.IsNullOrEmpty(version))
                {
                    version = RunCapture(Path.Combine(root, "cmd", "git.exe"), "--version");
                }
                return (bash, version);
            }
        }
        return (null, null);
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

    private static string? RunCapture(string exe, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
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
