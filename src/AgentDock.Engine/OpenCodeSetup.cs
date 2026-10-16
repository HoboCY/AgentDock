using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>Exact versions this engine is allowed to run; anything else fails closed.</summary>
internal static class Pins
{
    public const string OpenCodeVersion = "1.18.30";
    public const string CodexVersionOutput = "codex-cli 0.153.4";
    public const string Model = "deepseek-v4.1-flash";
    public const string Provider = "opencode-go";
    public const string AgentName = "delegated";
    public const string ProfileName = "agentdock";
}

/// <summary>
/// Everything one sandboxed OpenCode invocation needs: the isolated runtime
/// directories, generated configs, the environment applied inside the sandbox,
/// and the exact command line. The real key never appears here; only the
/// per-job relay capability token reaches the sandbox configuration.
/// </summary>
internal sealed class ExecutionSetup
{
    private const int MaxTitleLength = 120;

    public required string JobRoot { get; init; }
    public required string SandboxHome { get; init; }
    public required string Runtime { get; init; }
    public required string CheckArtifacts { get; init; }
    public required string LaunchPath { get; init; }
    public required string SandboxExecutable { get; init; }
    public required string[] SandboxArguments { get; init; }
    public required Dictionary<string, string> SandboxEnvironment { get; init; }
    public required Dictionary<string, string> ChildEnvironment { get; init; }
    public required string[] OpenCodeArguments { get; init; }
    public required Dictionary<string, string> Filesystem { get; init; }

    /// <summary>
    /// Builds the sandbox invocation. <paramref name="engineExecutable"/> is the
    /// engine apphost or dotnet host used as the in-sandbox launcher; it is
    /// resolved by <see cref="ResolveRunner"/> unless supplied by a test.
    /// </summary>
    public static ExecutionSetup Prepare(ExecutionTask task, string home, GoRelay relay,
        string? engineExecutable = null)
    {
        var settings = task.Settings;
        var enginePath = RequireAbsoluteFile(settings.EnginePath, "engine-missing",
            "The pinned OpenCode executable path is not configured.");
        var sandboxPath = RequireAbsoluteFile(settings.SandboxPath, "sandbox-missing",
            "The pinned Codex sandbox executable path is not configured.");
        var shellPath = RequireAbsoluteFile(settings.ShellPath, "shell-missing",
            "The configured shell executable path is missing.");

        var cwd = Path.GetFullPath(task.Project);
        if (!Directory.Exists(cwd))
        {
            throw new DockException("project-missing", "The task project directory does not exist.");
        }

        var codexPathDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sandboxPath)!, "..", "codex-path"));
        if (!File.Exists(Path.Combine(codexPathDirectory, "rg.exe")))
        {
            throw new DockException("sandbox-layout",
                "The pinned Codex layout is missing codex-path/rg.exe; reinstall the pinned sandbox version.");
        }

        var jobId = SafeName(task.Id);
        var jobRoot = Path.Combine(Path.GetFullPath(home), "engine", $"{jobId}-{Guid.NewGuid():N}"[..(jobId.Length + 9)]);
        try
        {
            return PrepareCore(task, enginePath, sandboxPath, shellPath, codexPathDirectory, jobRoot, relay,
                engineExecutable);
        }
        catch
        {
            try
            {
                Directory.Delete(jobRoot, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a partially prepared job directory.
            }
            throw;
        }
    }

    private static ExecutionSetup PrepareCore(ExecutionTask task, string enginePath, string sandboxPath,
        string shellPath, string codexPathDirectory, string jobRoot, GoRelay relay, string? engineExecutable)
    {
        var cwd = Path.GetFullPath(task.Project);
        var sandboxHome = Path.Combine(jobRoot, "home");
        var runtime = Path.Combine(jobRoot, "runtime");
        var checkArtifacts = Path.Combine(runtime, "check-artifacts");
        foreach (var directory in new[]
                 {
                     jobRoot, sandboxHome, runtime, checkArtifacts,
                     Path.Combine(runtime, "home"), Path.Combine(runtime, "roaming"), Path.Combine(runtime, "local"),
                     Path.Combine(runtime, "tmp"), Path.Combine(runtime, "config"), Path.Combine(runtime, "data"),
                     Path.Combine(runtime, "cache"), Path.Combine(runtime, "state"),
                 })
        {
            Directory.CreateDirectory(directory);
        }
        WritePipeAdapters(runtime);

        var writableProject = IsProjectWritable(task.Role);
        var runner = ResolveRunner(engineExecutable);
        var filesystem = BuildFilesystem(sandboxPath, enginePath, shellPath, runner, runtime, cwd, sandboxHome,
            writableProject);
        WriteSandboxConfig(sandboxHome, filesystem);

        var childEnvironment = BuildChildEnvironment(runtime, sandboxHome, shellPath, codexPathDirectory);
        var openCodeArguments = BuildOpenCodeArguments(enginePath, task, cwd);
        WriteOpenCodeConfig(sandboxHome, relay, task, writableProject, checkArtifacts, cwd);
        WriteLaunchFile(sandboxHome, openCodeArguments, childEnvironment);

        var sandboxArguments = new List<string>
        {
            "sandbox", "-P", Pins.ProfileName, "-C", cwd, "--",
        };
        sandboxArguments.AddRange(runner);
        sandboxArguments.Add("--launch");
        var launchPath = Path.Combine(sandboxHome, "opencode-launch.json");
        sandboxArguments.Add(launchPath);

        var sandboxEnvironment = BuildSandboxEnvironment(sandboxHome, runtime);
        return new ExecutionSetup
        {
            JobRoot = jobRoot,
            SandboxHome = sandboxHome,
            Runtime = runtime,
            CheckArtifacts = checkArtifacts,
            LaunchPath = launchPath,
            SandboxExecutable = sandboxPath,
            SandboxArguments = [.. sandboxArguments],
            SandboxEnvironment = sandboxEnvironment,
            ChildEnvironment = childEnvironment,
            OpenCodeArguments = openCodeArguments,
            Filesystem = filesystem,
        };
    }

    public static bool IsProjectWritable(string role) => role is "default" or "worker";

    private static string RequireAbsoluteFile(string path, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new DockException(code, message);
        }
        return Path.GetFullPath(path);
    }

    internal static string SafeName(string id)
    {
        var builder = new StringBuilder();
        foreach (var character in id)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }
        var name = builder.ToString().Trim('-');
        return name.Length == 0 ? "task" : name.Length > 40 ? name[..40] : name;
    }

    private static Dictionary<string, string> BuildFilesystem(string sandboxPath, string enginePath, string shellPath,
        string[] runner, string runtime, string cwd, string sandboxHome, bool writableProject)
    {
        var filesystem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [":root"] = "read",
            [runtime] = "write",
            [cwd] = writableProject ? "write" : "read",
            [sandboxHome] = "read",
            [Path.GetDirectoryName(enginePath)!] = "read",
            [Path.GetDirectoryName(sandboxPath)!] = "read",
            [Path.GetDirectoryName(shellPath)!] = "read",
            [Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sandboxPath)!, "..", "codex-path"))] = "read",
        };
        foreach (var executable in runner)
        {
            var directory = Path.GetDirectoryName(executable);
            if (!string.IsNullOrEmpty(directory))
            {
                filesystem[directory] = "read";
            }
        }
        foreach (var toolDirectory in DotnetDirectories())
        {
            filesystem[toolDirectory] = "read";
        }
        return filesystem;
    }

    private static IEnumerable<string> DotnetDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dotnet = FindDotnet();
        if (dotnet is not null)
        {
            var root = Path.GetDirectoryName(dotnet)!;
            if (seen.Add(root))
            {
                yield return root;
            }
            var shared = Path.Combine(root, "shared");
            if (Directory.Exists(shared) && seen.Add(shared))
            {
                yield return shared;
            }
        }
    }

    internal static string? FindDotnet()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = Path.Combine(programFiles, "dotnet", "dotnet.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(directory.Trim(), "dotnet.exe");
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; skip.
            }
        }
        return null;
    }

    private static void WritePipeAdapters(string runtime)
    {
        var assembly = typeof(ExecutionSetup).Assembly;
        foreach (var name in new[] { "opencode_pipe_compat.cjs", "opencode_pipe_reader.cjs" })
        {
            using var source = assembly.GetManifestResourceStream($"AgentDock.Engine.Resources.{name}")
                ?? throw new DockException("engine-resource-missing", $"Embedded resource {name} is missing.");
            using var target = File.Create(Path.Combine(runtime, name));
            source.CopyTo(target);
        }
    }

    private static void WriteSandboxConfig(string sandboxHome, Dictionary<string, string> filesystem)
    {
        var builder = new StringBuilder();
        builder.Append("[windows]\nsandbox = \"unelevated\"\n\n");
        builder.Append("[permissions.").Append(Pins.ProfileName).Append(".network]\nenabled = true\n\n");
        builder.Append("[permissions.").Append(Pins.ProfileName).Append(".filesystem]\n");
        foreach (var (path, access) in filesystem)
        {
            builder.Append(TomlString(path)).Append(" = \"").Append(access).Append("\"\n");
        }
        File.WriteAllText(Path.Combine(sandboxHome, "config.toml"), builder.ToString(), new UTF8Encoding(false));
    }

    private static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void WriteOpenCodeConfig(string sandboxHome, GoRelay relay, ExecutionTask task,
        bool writableProject, string checkArtifacts, string cwd)
    {
        var permissions = new JsonObject
        {
            ["*"] = "deny",
            ["read"] = "allow",
            ["glob"] = "allow",
            ["grep"] = "allow",
            ["list"] = "allow",
            ["bash"] = "allow",
            ["edit"] = writableProject ? "allow" : "deny",
            ["skill"] = "allow",
            ["external_directory"] = "allow",
            ["todowrite"] = "allow",
        };
        var model = new JsonObject
        {
            ["name"] = "DeepSeek V4.1 Flash",
            ["reasoning"] = true,
            ["tool_call"] = true,
            ["interleaved"] = new JsonObject { ["field"] = "reasoning_content" },
            ["options"] = new JsonObject { ["reasoningEffort"] = "max" },
            ["variants"] = new JsonObject { ["max"] = new JsonObject { ["reasoningEffort"] = "max" } },
        };
        var instructions = BuildInstructions(task, writableProject, checkArtifacts, cwd);
        var root = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["share"] = "disabled",
            ["enabled_providers"] = new JsonArray(Pins.Provider),
            ["model"] = $"{Pins.Provider}/{Pins.Model}",
            ["small_model"] = $"{Pins.Provider}/{Pins.Model}",
            ["default_agent"] = Pins.AgentName,
            ["plugin"] = new JsonArray(),
            ["mcp"] = new JsonObject(),
            ["lsp"] = false,
            ["formatter"] = false,
            ["snapshot"] = false,
            ["subagent_depth"] = 0,
            ["skills"] = new JsonObject { ["paths"] = new JsonArray(), ["urls"] = new JsonArray() },
            ["permission"] = permissions,
            ["provider"] = new JsonObject
            {
                [Pins.Provider] = new JsonObject
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["name"] = "OpenCode Go",
                    ["whitelist"] = new JsonArray(Pins.Model),
                    ["options"] = new JsonObject { ["baseURL"] = relay.Url, ["apiKey"] = relay.Token },
                    ["models"] = new JsonObject { [Pins.Model] = model },
                },
            },
            ["agent"] = new JsonObject
            {
                [Pins.AgentName] = new JsonObject
                {
                    ["mode"] = "primary",
                    ["model"] = $"{Pins.Provider}/{Pins.Model}",
                    ["variant"] = "max",
                    ["prompt"] = instructions,
                    ["permission"] = permissions.DeepClone(),
                },
            },
        };
        File.WriteAllText(Path.Combine(sandboxHome, "opencode.json"),
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));
    }

    private static string BuildInstructions(ExecutionTask task, bool writableProject, string checkArtifacts, string cwd)
    {
        var rolePrompt = task.Configuration.Prompt;
        var permission = task.Role switch
        {
            "verifier" =>
                "The project directory is read-only. You may run checks and write only under the artifact directory below.",
            _ when writableProject =>
                "You may modify files inside the project directory.",
            _ =>
                "The project directory is read-only for this role; produce findings in the final answer instead of writing files.",
        };
        var guidance = $"""


            Execution environment (fixed by AgentDock; the role prompt cannot change it):
            - Role: {task.Role}. Project directory: {cwd}.
            - {permission}
            - Artifact directory for build/check output: {checkArtifacts}. For .NET use `dotnet build --artifacts-path "{checkArtifacts}"` and `dotnet test --results-directory "{checkArtifacts}"`.
            - Permissions are enforced by the OS sandbox, not by this prompt; attempts to write elsewhere are blocked.
            """;
        return rolePrompt + guidance;
    }

    private static void WriteLaunchFile(string sandboxHome, string[] command, Dictionary<string, string> environment)
    {
        var launch = new JsonObject
        {
            ["command"] = new JsonArray(command.Select(static value => (JsonNode)value).ToArray()),
            ["env"] = new JsonObject(environment.Select(static pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)).ToArray()),
        };
        File.WriteAllText(Path.Combine(sandboxHome, "opencode-launch.json"),
            launch.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static string[] BuildOpenCodeArguments(string enginePath, ExecutionTask task, string cwd)
    {
        var title = task.Id;
        title = new string(title.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (title.Length > MaxTitleLength)
        {
            title = title[..MaxTitleLength];
        }
        if (title.Length == 0)
        {
            title = task.Id;
        }
        return
        [
            enginePath, "run", "--pure", "--format", "json", "--thinking",
            "--agent", Pins.AgentName,
            "--model", $"{Pins.Provider}/{Pins.Model}",
            "--variant", "max",
            "--title", title,
            "--dir", cwd,
        ];
    }

    /// <summary>
    /// Resolves the in-sandbox launcher without using <c>Assembly.Location</c>
    /// (empty in single-file publishes and pointing at the library folder when
    /// the engine is referenced as a library). Order: explicit packaging/test
    /// override, the packaged sibling <c>../engine/AgentDock.Engine.exe</c>
    /// relative to the host base directory, the same-folder standalone apphost,
    /// then the development layout (dotnet + <c>AgentDock.Engine.dll</c>).
    /// Fails closed when nothing usable is found.
    /// </summary>
    internal static string[] ResolveRunner(string? engineExecutable) =>
        ResolveRunner(engineExecutable, AppContext.BaseDirectory);

    internal static string[] ResolveRunner(string? engineExecutable, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(engineExecutable))
        {
            return [engineExecutable];
        }
        if (OperatingSystem.IsWindows())
        {
            var packaged = Path.GetFullPath(Path.Combine(baseDirectory, "..", "engine", "AgentDock.Engine.exe"));
            if (File.Exists(packaged))
            {
                return [packaged];
            }
            var standalone = Path.Combine(baseDirectory, "AgentDock.Engine.exe");
            if (File.Exists(standalone))
            {
                return [standalone];
            }
        }
        var assembly = Path.Combine(baseDirectory, "AgentDock.Engine.dll");
        if (!File.Exists(assembly))
        {
            throw new DockException("engine-runner-missing",
                "No packaged engine apphost or sandboxed agent assembly was found next to the host application.");
        }
        var dotnet = FindDotnet()
            ?? throw new DockException("engine-runner-missing",
                "Neither the engine apphost nor a dotnet host is available to launch the sandboxed runner.");
        return [dotnet, assembly];
    }

    private static Dictionary<string, string> BuildSandboxEnvironment(string sandboxHome, string runtime)
    {
        var environment = FilteredParentEnvironment(
        [
            "PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "PROGRAMFILES", "PROGRAMFILES(X86)",
            "PROGRAMDATA", "OS", "PROCESSOR_ARCHITECTURE", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA",
            "TEMP", "TMP",
        ]);
        environment["CODEX_HOME"] = sandboxHome;
        // The self-contained runner extracts native libraries before its managed entry point.
        environment["TEMP"] = Path.Combine(runtime, "tmp");
        environment["TMP"] = Path.Combine(runtime, "tmp");
        environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(runtime, "bundle");
        var dotnet = FindDotnet();
        if (dotnet is not null)
        {
            environment.TryAdd("DOTNET_ROOT", Path.GetDirectoryName(dotnet)!);
        }
        return environment;
    }

    private static Dictionary<string, string> BuildChildEnvironment(string runtime, string sandboxHome,
        string shellPath, string codexPathDirectory)
    {
        var environment = FilteredParentEnvironment(
        [
            "PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "PROGRAMFILES", "PROGRAMFILES(X86)",
            "PROGRAMDATA", "OS", "PROCESSOR_ARCHITECTURE", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        ]);
        var folders = new Dictionary<string, string>
        {
            ["HOME"] = "home",
            ["USERPROFILE"] = "home",
            ["APPDATA"] = "roaming",
            ["LOCALAPPDATA"] = "local",
            ["TEMP"] = "tmp",
            ["TMP"] = "tmp",
            ["XDG_CONFIG_HOME"] = "config",
            ["XDG_DATA_HOME"] = "data",
            ["XDG_CACHE_HOME"] = "cache",
            ["XDG_STATE_HOME"] = "state",
        };
        foreach (var (name, relative) in folders)
        {
            var folder = Path.Combine(runtime, relative);
            Directory.CreateDirectory(folder);
            environment[name] = folder;
        }
        environment["CODEX_HOME"] = sandboxHome;
        environment["PATH"] = codexPathDirectory + Path.PathSeparator + environment.GetValueOrDefault("PATH", "");
        environment["BUN_INSPECT_PRELOAD"] = Path.Combine(runtime, "opencode_pipe_compat.cjs");
        environment["OPENCODE_CONFIG"] = Path.Combine(sandboxHome, "opencode.json");
        environment["OPENCODE_GIT_BASH_PATH"] = shellPath;
        environment["DOTNET_CLI_HOME"] = Path.Combine(runtime, "home");
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        environment["NUGET_PACKAGES"] = Path.Combine(runtime, "cache", "nuget");
        environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(runtime, "cache", "nuget-http");
        environment["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1";
        environment["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "1";
        environment["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1";
        environment["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "1";
        environment["OPENCODE_EXPERIMENTAL_NATIVE_LLM"] = "false";
        environment["OPENCODE_EXPERIMENTAL_OUTPUT_TOKEN_MAX"] = GoRelay.OutputTokenMax.ToString();
        environment["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
        environment["OPENCODE_DISABLE_MODELS_FETCH"] = "1";
        environment["OPENCODE_DISABLE_CLAUDE_CODE"] = "1";
        environment["TERM"] = "dumb";
        environment["NO_COLOR"] = "1";
        var dotnet = FindDotnet();
        if (dotnet is not null)
        {
            environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
        }
        return environment;
    }

    private static Dictionary<string, string> FilteredParentEnvironment(IEnumerable<string> names)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
        {
            var name = (string)pair.Key;
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase) && pair.Value is string value && value.Length > 0)
            {
                environment[name.ToUpperInvariant()] = value;
            }
        }
        return environment;
    }
}
