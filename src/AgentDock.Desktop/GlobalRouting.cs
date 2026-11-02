using System.Text;
using System.Text.Json;
using AgentDock.Core;

namespace AgentDock.Desktop;

public sealed record RoutingStatus(string Path, bool Enabled, string? Problem = null, bool UserManaged = false);

/// <summary>Owns one marked instruction block. Saves its predecessor before changing the active file.</summary>
public sealed class GlobalRouting(string codexHome, string dataHome)
{
    public const string Start = "<!-- external-agent-routing:start -->";
    public const string End = "<!-- external-agent-routing:end -->";
    private const string PreviousInstructions = """
        <!-- external-agent-routing:start -->
        <!-- agentdock:managed-v1 -->
        ## External agent routing

        Keep the primary agent's configured model and ChatGPT authentication. Native collaboration.spawn_agent remains available only for architect, with its existing role settings.

        Delegate clear, narrowly scoped, independent subtasks for default, explorer, worker, reviewer and verifier through AgentDock MCP start_task. Supply an absolute cwd, role, unique request_id, explicit task scope, owned files and acceptance criteria. The main agent remains responsible for planning, integration and final verification. Keep atomic tasks, browser, desktop and app-connector work with the main agent.

        Use wait_task/get_task with event cursors until each accepted task reaches a terminal state; drain hasMore pages. Do not treat acceptance or a running task as completion. Reuse request_id only for an identical retry; failed or interrupted tasks need an explicit new user request before dispatching again. Do not switch models or backends automatically. If AgentDock is unavailable, report the blocker and continue independent main-agent work.

        These defaults apply across projects, subject to higher-priority instructions and project-specific restrictions. Send only task-relevant code, documentation and redacted evidence; never send secrets, credentials or unrelated data.
        <!-- external-agent-routing:end -->
        """;

    private const string DispatchInstructions = """
        Before each AgentDock MCP start_task, print SUBAGENT_DISPATCH with task_name, agent_type, model and reasoning_effort. Read model and reasoning_effort from AgentDock status.executionProfile.model and status.executionProfile.reasoningEffort; executionProfile.provider identifies the external provider. These fields describe the engine for the next dispatch, not historical tasks. For this external route, use the reported execution profile instead of native Codex agent TOMLs, spawn defaults or the primary model. If an older host omits these fields, verify its actual engine profile and report unknown only for settings that cannot be verified. This reporting rule does not authorize a model or backend change.
        """;

    private static readonly string InlineInstructions = PreviousInstructions
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("<!-- agentdock:managed-v1 -->", "<!-- agentdock:managed-v2 -->", StringComparison.Ordinal)
        .Replace("\n\nThese defaults apply", "\n\n" + DispatchInstructions + "\n\nThese defaults apply", StringComparison.Ordinal);

    public const string EntryInstructions = """
        ## Global scope and delegation

        These are global defaults. More specific project and directory instructions override conflicting defaults; higher-priority instructions and explicit user requests take precedence. The companion documents below share this global scope.

        - Before choosing roles, planning delegation, or dispatching any native or external subagent, MUST read [AGENT-ROLES.md](AGENT-ROLES.md) and [AGENTDOCK.md](AGENTDOCK.md) in full.
        - Before monitoring or controlling an existing AgentDock task, MUST read [AGENTDOCK.md](AGENTDOCK.md).
        - Resolve these links beside this global AGENTS.md, not from the project working directory. The default directory is `~/.codex`, unless `CODEX_HOME` selects another location.
        - If a required document is missing or unreadable, report its resolved path and continue suitable work in the primary agent. Do not delegate or operate AgentDock until the required documents have been read.
        """;

    public static readonly string Instructions = Start + "\n<!-- agentdock:managed-v3 -->\n" + EntryInstructions + "\n" + End;

    public const string DelegationInstructions = """
        # AgentDock delegation

        Apply these global defaults subject to higher-priority instructions, explicit user requests, and applicable project or directory instructions. Role responsibilities are defined in [AGENT-ROLES.md](AGENT-ROLES.md), beside this document.

        ## Routing

        - Keep the primary agent's configured model and ChatGPT authentication. Do not switch models or backends automatically.
        - Use native `collaboration.spawn_agent` only for `architect`, with its existing role settings.
        - Send clear, narrowly scoped, independent subtasks for `default`, `explorer`, `worker`, `reviewer`, and `verifier` through AgentDock MCP `start_task`.
        - The primary agent owns planning, integration, and final verification. Keep atomic tasks, browser, desktop, and app-connector work in the primary agent.

        ## Before each external dispatch

        1. Call `status`. Read `executionProfile.provider`, `executionProfile.model`, and `executionProfile.reasoningEffort` for the next dispatch, not historical tasks. Do not infer them from native agent TOMLs, spawn defaults, or the primary model. If an older host omits these fields, verify its actual engine profile; report unknown only for settings that cannot be verified.
        2. Print `SUBAGENT_DISPATCH` with `task_name`, `agent_type`, `model`, and `reasoning_effort`. Reporting the execution profile does not authorize a model or backend change.
        3. Call `start_task` with a short `title`, an existing absolute `cwd`, the selected `role`, a unique `request_id`, and a `prompt` containing the task scope, owned files, and acceptance criteria.

        ## Completion and retries

        - Use `wait_task` or `get_task` with event cursors until every accepted task reaches a terminal state; drain pages while `hasMore` is true. Acceptance or a running state is not completion, and a terminal failure is not success.
        - Reuse a `request_id` only for an identical retry with unchanged parameters. Failed or interrupted tasks require an explicit new user request before dispatching again.
        - If AgentDock is unavailable, report the blocker and continue independent work in the primary agent. Do not silently use another backend.

        ## Data boundary

        Send only task-relevant code, documentation, and redacted evidence; never send secrets, credentials, or unrelated data. Role instructions do not grant permissions beyond the host's actual access controls.

        ## Maintenance

        This standalone file is user-maintained. AgentDock creates missing companion documents when enabling or upgrading its managed entry, preserves existing documents, and keeps them when disabling that entry.
        """;

    public const string RoleInstructions = """
        # Subagent roles

        Choose only roles relevant to the task. Use [AGENTDOCK.md](AGENTDOCK.md) for execution routes, model configuration, primary-agent responsibilities, and task lifecycle. Resolve this link beside this document.

        | Role | Responsibility |
        | --- | --- |
        | `explorer` | Investigate behavior and locate code paths. |
        | `architect` | Resolve architecture and design decisions. |
        | `worker` | Implement features, fixes, and refactoring. |
        | `reviewer` | Independently review correctness and regressions. |
        | `verifier` | Run tests, builds, and runtime validation. |
        | `default` | Handle other clearly bounded tasks. |

        Role names define responsibilities, not model selection or additional execution permissions.
        """;

    private static readonly (string Name, string Content)[] Documents =
        [("AGENTDOCK.md", DelegationInstructions), ("AGENT-ROLES.md", RoleInstructions)];

    private const string Transitional = """
        <!-- external-agent-routing:start -->
        ## External agent routing

        Keep the primary agent's configured model and ChatGPT authentication. Native collaboration.spawn_agent remains available only for architect, with its existing role settings.

        For default, explorer, worker, reviewer and verifier, handle work in the primary agent until the user selects and validates a replacement external-agent integration. Do not switch models or backends automatically. Keep browser, desktop and app-connector work with the primary agent.
        <!-- external-agent-routing:end -->
        """;

    private string BackupPath => Path.Combine(dataHome, "onboarding-routing-backup.json");
    private string ActivePath
    {
        get
        {
            var alternate = Path.Combine(codexHome, "AGENTS.override.md");
            return File.Exists(alternate) && !string.IsNullOrWhiteSpace(File.ReadAllText(alternate))
                ? alternate : Path.Combine(codexHome, "AGENTS.md");
        }
    }

    public RoutingStatus Inspect()
    {
        var path = Path.Combine(codexHome, "AGENTS.md");
        try
        {
            path = ActivePath;
            var snapshot = Read(path);
            var block = FindBlock(snapshot.Text);
            var outside = "\n" + Normalize(block is null ? snapshot.Text : snapshot.Text.Replace(block, "", StringComparison.Ordinal)) + "\n";
            const string heading = "\n## Global scope and delegation\n";
            if (outside.Contains(heading, StringComparison.Ordinal))
            {
                if (block is not null || !outside.Contains(Normalize(EntryInstructions), StringComparison.Ordinal) ||
                    outside.IndexOf(heading, StringComparison.Ordinal) != outside.LastIndexOf(heading, StringComparison.Ordinal))
                    throw new InvalidOperationException("全局入口重复或已被修改，请先检查冲突。");
                CheckDocuments();
                return new(path, true, UserManaged: true);
            }
            var backup = LoadBackup();
            if (backup is not null && !SamePath(backup.Path, path))
                return new(path, false, "生效的全局规则文件已变化。请先处理原文件中的 AgentDock 规则，再重新配置。");
            if (block is null) return new(path, false);
            var own = IsManaged(block);
            if (Normalize(block) == Normalize(Instructions)) CheckDocuments();
            if (own && backup is null) return new(path, true, "规则已启用，但恢复记录缺失；关闭前需人工检查原规则。");
            if (own && backup is not null && block != backup.WrittenBlock && block != backup.PreviousWrittenBlock)
                return new(path, true, "恢复记录与当前规则不同，已保留文件，请先检查上次配置结果。");
            return new(path, own, own || Normalize(block) == Normalize(Transitional) ? null : "已有其他委派规则或区块被手动修改，请先检查冲突。");
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
        {
            return new(path, false, error is InvalidOperationException ? error.Message : "无法读取全局委派规则或恢复记录，请检查文件权限和内容。");
        }
    }

    /// <summary>Updates only an already-enabled, recognized block with a matching recovery journal.</summary>
    public RoutingStatus UpgradeEnabled()
    {
        var state = Inspect();
        if (!state.Enabled || state.Problem is not null || state.UserManaged) return state;
        try
        {
            var snapshot = Read(state.Path);
            var old = FindBlock(snapshot.Text);
            var backup = LoadBackup() ?? throw new InvalidOperationException("规则恢复记录已变化，请刷新后检查。");
            if (!SamePath(backup.Path, state.Path) || old is null || !IsManaged(old) ||
                !IsManaged(backup.WrittenBlock) ||
                (backup.PreviousWrittenBlock is not null && !IsManaged(backup.PreviousWrittenBlock)) ||
                (old != backup.WrittenBlock && old != backup.PreviousWrittenBlock))
                throw new InvalidOperationException("规则或恢复记录在检查后发生变化，已保留文件，请刷新后检查。");

            var written = Instructions.Replace("\n", snapshot.NewLine);
            if (Normalize(old) == Normalize(Instructions) && backup.PreviousWrittenBlock is null) return state;
            CreateMissingDocuments();
            if (backup.WrittenBlock != written || backup.PreviousWrittenBlock is null)
            {
                // Journal both versions before replacing the file so an interrupted upgrade can resume.
                if (backup.AppendedText is not null)
                    backup.AppendedText = backup.AppendedText.Replace(backup.WrittenBlock, written, StringComparison.Ordinal);
                backup.PreviousWrittenBlock = old;
                backup.WrittenBlock = written;
                AtomicJson.Write(BackupPath, backup);
            }

            if (old != written) Write(snapshot, snapshot.Text.Replace(old, written, StringComparison.Ordinal));
            backup.PreviousWrittenBlock = null;
            AtomicJson.Write(BackupPath, backup);
            return Inspect();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
        {
            return new(state.Path, state.Enabled, error is InvalidOperationException ? error.Message : "无法更新全局委派规则，已保留恢复记录，请检查文件权限和内容。");
        }
    }

    public RoutingStatus Enable()
    {
        var state = UpgradeEnabled();
        if (state.Problem is not null) throw new InvalidOperationException(state.Problem);
        if (state.Enabled) return state;
        var snapshot = Read(state.Path);
        var old = FindBlock(snapshot.Text);
        if (old is not null && Normalize(old) != Normalize(Transitional))
            throw new InvalidOperationException("委派规则在检查后发生变化，请刷新后重试。");
        var written = Instructions.Replace("\n", snapshot.NewLine);
        var backup = LoadBackup();
        if (backup is not null && (backup.OriginalBlock != old || backup.WrittenBlock != written))
            throw new InvalidOperationException("恢复记录与当前规则不同，请先检查上次配置结果。");
        var addition = (snapshot.Text.Length == 0 || snapshot.Text.EndsWith('\n') ? "" : snapshot.NewLine) + written + snapshot.NewLine;
        backup ??= new RoutingBackup { Path = state.Path, OriginalBlock = old, WrittenBlock = written, AppendedText = old is null ? addition : null };
        CreateMissingDocuments();
        AtomicJson.Write(BackupPath, backup);
        var next = old is null
            ? snapshot.Text + addition
            : snapshot.Text.Replace(old, written, StringComparison.Ordinal);
        Write(snapshot, next);
        return Inspect();
    }

    public RoutingStatus Disable()
    {
        var state = UpgradeEnabled();
        if (state.Problem is not null) throw new InvalidOperationException(state.Problem);
        if (state.UserManaged) throw new InvalidOperationException("当前入口由你手动维护；如需关闭，请编辑全局规则中的 Global scope and delegation 段落。");
        var backup = LoadBackup();
        if (backup is null) return state;
        var snapshot = Read(state.Path);
        var current = FindBlock(snapshot.Text);
        if (current == backup.OriginalBlock)
        {
            File.Delete(BackupPath); // An earlier disable completed before deleting its journal.
            return Inspect();
        }
        if (current != backup.WrittenBlock)
            throw new InvalidOperationException("AgentDock 区块已被修改，已保留文件，请人工检查后再关闭。");
        var remove = backup.OriginalBlock is null && backup.AppendedText is { Length: > 0 } appended && snapshot.Text.Contains(appended, StringComparison.Ordinal)
            ? appended : current;
        Write(snapshot, snapshot.Text.Replace(remove, backup.OriginalBlock ?? "", StringComparison.Ordinal));
        File.Delete(BackupPath);
        return Inspect();
    }

    private RoutingBackup? LoadBackup()
    {
        if (!File.Exists(BackupPath)) return null;
        var backup = JsonSerializer.Deserialize<RoutingBackup>(File.ReadAllBytes(BackupPath), JsonDefaults.Options);
        if (backup is null || string.IsNullOrWhiteSpace(backup.Path) || string.IsNullOrWhiteSpace(backup.WrittenBlock) ||
            !Path.IsPathFullyQualified(backup.Path) || backup.Path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidOperationException("全局规则恢复记录不完整，请检查文件。");
        return backup;
    }

    private static string? FindBlock(string text)
    {
        var start = text.IndexOf(Start, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 && end < 0) return null;
        if (start < 0 || end < start || text.IndexOf(Start, start + Start.Length, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("委派规则标记重复或不完整，已保留原文件，请先修复标记。");
        return text[start..(end + End.Length)];
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();
    private static bool IsManaged(string value) => Normalize(value) == Normalize(Instructions) ||
        Normalize(value) == Normalize(InlineInstructions) || Normalize(value) == Normalize(PreviousInstructions);
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void CheckDocuments()
    {
        foreach (var (name, _) in Documents)
            if (string.IsNullOrWhiteSpace(Read(Path.Combine(codexHome, name)).Text))
                throw new InvalidOperationException($"配套文档缺失或为空：{Path.Combine(codexHome, name)}。请补齐后重新检查。");
    }

    private void CreateMissingDocuments()
    {
        var snapshots = Documents.Select(document => (Snapshot: Read(Path.Combine(codexHome, document.Name)), document.Content)).ToArray();
        foreach (var (snapshot, _) in snapshots)
            if (File.Exists(snapshot.Path) && string.IsNullOrWhiteSpace(snapshot.Text))
                throw new InvalidOperationException($"已有配套文档为空，已保留文件：{snapshot.Path}。请补齐后重新检查。");
        foreach (var (snapshot, content) in snapshots)
            if (!File.Exists(snapshot.Path)) Write(snapshot, content + "\n", overwrite: false);
    }

    private sealed record Snapshot(string Path, byte[] Bytes, string Text, Encoding Encoding, bool HasPreamble)
    {
        public string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static Snapshot Read(string path)
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        var preamble = encoding.GetPreamble();
        return new(path, bytes, text, encoding, preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble));
    }

    private static void Write(Snapshot snapshot, string text, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
        var temporary = snapshot.Path + ".agentdock-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (snapshot.HasPreamble) stream.Write(snapshot.Encoding.GetPreamble());
                stream.Write(snapshot.Encoding.GetBytes(text));
                stream.Flush(true);
            }
            var current = File.Exists(snapshot.Path) ? File.ReadAllBytes(snapshot.Path) : [];
            if (!current.AsSpan().SequenceEqual(snapshot.Bytes))
                throw new InvalidOperationException("规则文件在配置期间发生变化，请刷新后重试。");
            File.Move(temporary, snapshot.Path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public sealed class RoutingBackup
    {
        public string Path { get; set; } = "";
        public string? OriginalBlock { get; set; }
        public string WrittenBlock { get; set; } = "";
        public string? PreviousWrittenBlock { get; set; }
        public string? AppendedText { get; set; }
    }
}
