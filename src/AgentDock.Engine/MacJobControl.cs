using System.Text;
using System.Text.Json;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>Outcome of reading one protected metadata file.</summary>
internal enum MacMetadataStatus { Missing, Ok, Invalid }

/// <summary>Outcome of reading one owned control file.</summary>
internal enum MacReadStatus { Missing, Ok, Invalid }

/// <summary>The exact command one launchd job runs, supplied by the host caller.</summary>
internal sealed record MacJobCommand(string Executable, string[] Arguments, string WorkingDirectory,
    Dictionary<string, string> Environment);

/// <summary>
/// One AgentDock macOS job description. Written by the host side under
/// <c>&lt;jobRoot&gt;/control</c> (outside the task-writable runtime), read by
/// both <c>--mac-watchdog</c> and <c>--mac-job</c>. The file itself is protected
/// metadata: the task runtime must never be able to read or replace it.
/// </summary>
internal sealed record MacJobConfig(string Label, string JobRoot, string BootSession, string[] ProgramArguments, MacJobCommand Command);

/// <summary>
/// Paths, bounds and bounded file helpers shared by the macOS job backend.
/// All control files live under <c>&lt;jobRoot&gt;/control</c>, which is
/// deliberately outside any runtime directory the task may write to.
/// </summary>
internal static class MacJobControl
{
    /// <summary>
    /// Optional environment entry consumed by <c>MacJobProcess.Start</c>: the
    /// exact job root directory to use. It is stripped from the job command
    /// environment. When absent, a fresh directory under the user's temporary
    /// directory is created.
    /// </summary>
    public const string ReservedRootVariable = "AGENTDOCK_MAC_JOB_ROOT";

    public const int ConfigVersion = 2;
    public const long LogLimitBytes = 64 * 1024 * 1024;
    public const int PromptMaxBytes = 1024 * 1024;
    public const int ConfigMaxBytes = 4 * 1024 * 1024;
    public const int MetadataMaxBytes = 64 * 1024;
    public const int ErrorMaxChars = 8_000;
    public const int BootstrapDeadlineMs = 30_000;
    public const int GoDeadlineMs = 120_000;
    public const int IdentityDrainMs = 5_000;
    public const int LaunchctlTimeoutMs = 15_000;
    public const int FreezeRounds = 40;
    public const int KillRounds = 60;
    public const string LabelPrefix = "com.agentdock.mac-job.";
    public const string WatchdogErrorPrefix = "AgentDock mac watchdog: ";

    public static string DefaultJobRootBase => Path.Combine(Path.GetTempPath(), "agentdock-mac-jobs");

    public static string ControlDirectory(string jobRoot) => Path.Combine(jobRoot, "control");

    public static string ConfigPath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "job.json");

    public static string PromptPath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "prompt.bin");

    public static string GoPath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "go");

    public static string IdentityPath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "identity.json");

    public static string ExitCodePath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "exit.json");

    public static string PlistPath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "job.plist");

    public static string JobStdoutPath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "job.stdout");

    public static string JobStderrPath(string jobRoot) => Path.Combine(ControlDirectory(jobRoot), "job.stderr");

    public static string LabelFor(string jobRoot) => LabelPrefix + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ProjectPaths.Canonicalize(jobRoot))))[..32];

    public static bool IsValidLabelSuffix(string suffix)
    {
        if (suffix.Length is 0 or > 128)
        {
            return false;
        }
        foreach (var character in suffix)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>Owner-only mode; directories additionally get the execute bit.</summary>
    public static void SetPrivateMode(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (directory)
        {
            mode |= UnixFileMode.UserExecute;
        }
        File.SetUnixFileMode(path, mode);
    }

    /// <summary>Creates or replaces a small file atomically (write, fsync, rename).</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content, 0, content.Length);
            stream.Flush(flushToDisk: true);
        }
        SetPrivateMode(temporary, directory: false);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Reads one control file that must be a current-user-owned regular file no
    /// larger than <paramref name="maxBytes"/>. Symlinks, other owners and
    /// oversized files are rejected instead of followed.
    /// </summary>
    public static MacReadStatus TryReadOwnedFile(string path, int maxBytes, out byte[] content, out string? error)
    {
        content = [];
        error = null;
        if (!MacFileIdentity.TryStat(path, out var kind, out var ownerUid, out error))
        {
            return MacReadStatus.Invalid;
        }
        if (kind == MacFileKind.Missing)
        {
            return MacReadStatus.Missing;
        }
        if (kind != MacFileKind.RegularFile)
        {
            error = $"'{path}' is not a regular file.";
            return MacReadStatus.Invalid;
        }
        if (ownerUid != MacProcessProbe.EffectiveUserId)
        {
            error = $"'{path}' is not owned by the executing user.";
            return MacReadStatus.Invalid;
        }
        try
        {
            if (new FileInfo(path).Length > maxBytes)
            {
                error = $"'{path}' exceeds the {maxBytes}-byte bound.";
                return MacReadStatus.Invalid;
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > maxBytes)
            {
                error = $"'{path}' grew past the {maxBytes}-byte bound while being read.";
                return MacReadStatus.Invalid;
            }
            content = bytes;
            return MacReadStatus.Ok;
        }
        catch (Exception readError) when (readError is IOException or UnauthorizedAccessException)
        {
            error = $"reading '{path}' failed: {readError.Message}";
            return MacReadStatus.Invalid;
        }
    }

    /// <summary>Fail-closed precheck for a control file before it is parsed.</summary>
    public static void RequireOwnedRegularFile(string path)
    {
        if (!MacFileIdentity.TryStat(path, out var kind, out var ownerUid, out var error))
        {
            throw new DockException("mac-control-invalid", $"The control file '{path}' could not be inspected: {error}");
        }
        if (kind != MacFileKind.RegularFile)
        {
            throw new DockException("mac-control-invalid", $"The control file '{path}' is not a regular file.");
        }
        if (ownerUid != MacProcessProbe.EffectiveUserId)
        {
            throw new DockException("mac-control-invalid", $"The control file '{path}' is not owned by the executing user.");
        }
    }

    public static MacJobConfig ReadConfig(string controlPath)
    {
        var fullPath = Path.GetFullPath(controlPath);
        var status = TryReadOwnedFile(fullPath, ConfigMaxBytes, out var content, out var error);
        if (status != MacReadStatus.Ok)
        {
            throw new DockException("mac-config-invalid",
                $"The job control file could not be read: {error ?? "it is missing"}.");
        }
        try
        {
            using var document = JsonDocument.Parse(content);
            return ParseConfig(document.RootElement, fullPath);
        }
        catch (JsonException jsonError)
        {
            throw new DockException("mac-config-invalid", "The job control file is not valid JSON: " + jsonError.Message);
        }
    }

    private static MacJobConfig ParseConfig(JsonElement root, string controlPath)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The job control file must be a JSON object.");
        }
        if (!root.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt32(out var version) ||
            version != ConfigVersion)
        {
            throw Invalid("Unsupported job control version.");
        }
        if (!TryGetString(root, "label", out var label) || !label.StartsWith(LabelPrefix, StringComparison.Ordinal) ||
            !IsValidLabelSuffix(label[LabelPrefix.Length..]))
        {
            throw Invalid("Invalid launchd label.");
        }
        if (!TryGetString(root, "jobRoot", out var jobRootText) || !Path.IsPathFullyQualified(jobRootText))
        {
            throw Invalid("The job root must be an absolute path.");
        }
        var jobRoot = Path.GetFullPath(jobRootText);
        if (label != LabelFor(jobRoot) || !TryGetString(root, "bootSession", out var bootSession) || !Guid.TryParse(bootSession, out _))
            throw Invalid("The launchd label or boot identity does not match this job.");
        if (!string.Equals(Path.GetFullPath(ConfigPath(jobRoot)), controlPath, StringComparison.Ordinal))
        {
            throw Invalid("The job control file is not inside the declared job root.");
        }
        if (!TryGetStringArray(root, "programArguments", out var programArguments) || programArguments.Length < 3 ||
            !Path.IsPathFullyQualified(programArguments[0]) ||
            !string.Equals(programArguments[^2], "--mac-job", StringComparison.Ordinal) ||
            !string.Equals(programArguments[^1], controlPath, StringComparison.Ordinal))
        {
            throw Invalid("The launchd program arguments do not invoke --mac-job with this control file.");
        }
        if (!root.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The job control file has no command description.");
        }
        if (!TryGetString(commandElement, "executable", out var executable) || !Path.IsPathFullyQualified(executable))
        {
            throw Invalid("The task executable must be an absolute path.");
        }
        if (!TryGetString(commandElement, "workingDirectory", out var workingDirectory) ||
            !Path.IsPathFullyQualified(workingDirectory))
        {
            throw Invalid("The task working directory must be an absolute path.");
        }
        if (!TryGetStringArray(commandElement, "arguments", out var arguments))
        {
            throw Invalid("The task arguments are invalid.");
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (commandElement.TryGetProperty("environment", out var environmentElement))
        {
            if (environmentElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The task environment must be a JSON object.");
            }
            foreach (var property in environmentElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw Invalid("The task environment values must be strings.");
                }
                environment[property.Name] = property.Value.GetString()!;
            }
            if (environment.Count > 4096)
            {
                throw Invalid("The task environment has too many entries.");
            }
        }
        return new MacJobConfig(label, jobRoot, bootSession, programArguments,
            new MacJobCommand(executable, arguments, workingDirectory, environment));
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString() ?? "";
        return value.Length > 0;
    }

    private static bool TryGetStringArray(JsonElement root, string name, out string[] values)
    {
        values = [];
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            list.Add(item.GetString()!);
        }
        if (list.Count > 4096)
        {
            return false;
        }
        values = list.ToArray();
        return true;
    }

    private static DockException Invalid(string message) => new("mac-config-invalid", message);

    public static byte[] BuildIdentityJson(MacProcessIdentity identity)
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = ConfigVersion,
            ["pid"] = identity.Pid,
            ["pidVersion"] = identity.Version,
            ["coalitionId"] = identity.CoalitionId,
        };
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    public static byte[] BuildExitJson(int exitCode)
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = ConfigVersion,
            ["exitCode"] = exitCode,
        };
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    public static MacMetadataStatus TryReadIdentity(string jobRoot, out MacProcessIdentity identity, out string? error)
    {
        identity = default;
        var status = TryReadOwnedFile(IdentityPath(jobRoot), MetadataMaxBytes, out var content, out error);
        if (status != MacReadStatus.Ok)
        {
            error = status == MacReadStatus.Missing ? null : error;
            return status == MacReadStatus.Missing ? MacMetadataStatus.Missing : MacMetadataStatus.Invalid;
        }
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt32(out var version) ||
                version != ConfigVersion ||
                !root.TryGetProperty("pid", out var pidElement) || !pidElement.TryGetInt32(out var pid) || pid <= 0 ||
                !root.TryGetProperty("pidVersion", out var versionValueElement) ||
                !versionValueElement.TryGetUInt32(out var pidVersion) ||
                !root.TryGetProperty("coalitionId", out var coalitionElement) ||
                !coalitionElement.TryGetUInt64(out var coalitionId))
            {
                error = "The job identity metadata is invalid.";
                return MacMetadataStatus.Invalid;
            }
            identity = new MacProcessIdentity(pid, pidVersion, coalitionId);
            error = null;
            return MacMetadataStatus.Ok;
        }
        catch (JsonException jsonError)
        {
            error = "The job identity metadata is not valid JSON: " + jsonError.Message;
            return MacMetadataStatus.Invalid;
        }
    }

    public static MacMetadataStatus TryReadExitCode(string jobRoot, out int exitCode, out string? error)
    {
        exitCode = 0;
        var status = TryReadOwnedFile(ExitCodePath(jobRoot), MetadataMaxBytes, out var content, out error);
        if (status != MacReadStatus.Ok)
        {
            error = status == MacReadStatus.Missing ? null : error;
            return status == MacReadStatus.Missing ? MacMetadataStatus.Missing : MacMetadataStatus.Invalid;
        }
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt32(out var version) ||
                version != ConfigVersion ||
                !root.TryGetProperty("exitCode", out var exitElement) || !exitElement.TryGetInt32(out exitCode))
            {
                error = "The job exit metadata is invalid.";
                return MacMetadataStatus.Invalid;
            }
            error = null;
            return MacMetadataStatus.Ok;
        }
        catch (JsonException jsonError)
        {
            error = "The job exit metadata is not valid JSON: " + jsonError.Message;
            return MacMetadataStatus.Invalid;
        }
    }
}
