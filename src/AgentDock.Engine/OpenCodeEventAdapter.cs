using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// Translates pinned OpenCode 1.18.30 <c>run --format json</c> lines into the
/// bounded engine event vocabulary. Success is decided separately from streamed
/// items: only a final <c>step_finish</c> whose reason is exactly <c>stop</c>
/// with a zero exit and no terminal error can complete successfully.
///
/// Usage is per <c>step_finish</c> part: each part ID contributes exactly one
/// event carrying only that part's own components (input + cache read/write;
/// output + reasoning), and components that were never reported stay null
/// instead of a fake zero. The host sums one contribution per UsageId, so
/// identical replays are ignored and revisions replace the same part, so totals never double
/// count across steps.
/// </summary>
internal sealed partial class OpenCodeEventAdapter
{
    public const int MaxText = 48_000;
    public const int MaxToolOutput = 16_000;

    private static readonly HashSet<string> PathKeys = new(StringComparer.Ordinal)
    {
        "file", "files", "filepath", "filepaths", "file_path", "file_paths", "filename", "filenames",
        "path", "paths", "target", "targets",
    };

    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
    {
        "apply_patch", "delete", "edit", "multiedit", "patch", "rename", "write",
    };

    private readonly Dictionary<string, UsageComponents> _usageSeen = new(StringComparer.Ordinal);
    private sealed record UsageComponents(double? Input, double? Output, double? Reasoning, double? CacheRead, double? CacheWrite);
    private readonly HashSet<string> _startedTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finishedTools = new(StringComparer.Ordinal);

    private string? _lastStepReason;
    private bool _terminalError;

    public string? SessionId { get; private set; }

    public bool Completed { get; private set; }

    /// <summary>Latest non-reasoning text part (bounded), empty before any text.</summary>
    public string Result { get; private set; } = "";

    /// <summary>Bounded terminal error text when the stream reported one.</summary>
    public string? Failure { get; private set; }

    /// <summary>True only for terminal stop + zero exit + no terminal error.</summary>
    public bool CanComplete(int exitCode) =>
        exitCode == 0 && _lastStepReason == "stop" && !_terminalError;

    public IReadOnlyList<EngineEvent> Feed(JsonElement value)
    {
        if (Completed || value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        try
        {
            return FeedCore(value);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            // Malformed JSON values are tolerated and never affect completion.
            return [];
        }
    }

    public IReadOnlyList<EngineEvent> Feed(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return [];
        }
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            return Feed(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private IReadOnlyList<EngineEvent> FeedCore(JsonElement value)
    {
        var type = Normalize(GetString(value, "type"));
        var properties = GetObject(value, "properties");
        var part = GetObject(value, "part") ?? (properties is { } p ? GetObject(p, "part") : null);
        RememberSession(value, properties, part);

        switch (type)
        {
            case "error":
            case "session_error":
            {
                _terminalError = true;
                var errorValue = GetValue(value, "error") ?? (properties is { } pr ? GetValue(pr, "error") : null);
                var text = errorValue is null
                    ? GetString(value, "message") ?? "OpenCode reported an error"
                    : Compact(errorValue.Value);
                var bounded = Bound(text, MaxToolOutput);
                Failure = bounded;
                return [new EngineEvent("error", bounded)];
            }
            case "text":
            case "reasoning":
            {
                var text = GetString(part is { } pl ? pl : value, "text") ?? GetString(value, "text");
                if (string.IsNullOrEmpty(text))
                {
                    return [];
                }
                var bounded = Bound(text, MaxText);
                if (type == "text")
                {
                    Result = bounded;
                    return [new EngineEvent("text", bounded)];
                }
                return [new EngineEvent("reasoning", bounded)];
            }
            case "tool_use":
                return ToolEvents(value, part);
            case "step_finish":
                return StepFinish(value, part);
            default:
                return [];
        }
    }

    private IReadOnlyList<EngineEvent> StepFinish(JsonElement value, JsonElement? part)
    {
        var reason = part is { } p ? GetString(p, "reason") : null;
        reason ??= GetString(value, "reason");
        _lastStepReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim().ToLowerInvariant();

        var tokens = part is { } pc ? GetValue(pc, "tokens") : null;
        tokens ??= GetValue(value, "tokens");
        var partId = part is { } pi ? FirstString(pi, "id", "partID") : FirstString(value, "id", "partID");
        if (!TryReadUsage(tokens, partId, out var inputTokens, out var outputTokens))
        {
            return [];
        }
        return [new EngineEvent("usage", "",
            UsageId: partId ?? SessionId,
            InputTokens: inputTokens,
            OutputTokens: outputTokens)];
    }

    private IReadOnlyList<EngineEvent> ToolEvents(JsonElement value, JsonElement? part)
    {
        if (part is not { } partValue)
        {
            return [];
        }
        var state = GetObject(partValue, "state");
        if (state is null)
        {
            return [];
        }
        var status = Normalize(GetString(state.Value, "status"));
        if (status is not ("pending" or "running" or "completed" or "error"))
        {
            return [];
        }

        // A tool call after an observed stop means the stop was not final.
        if (_lastStepReason == "stop")
        {
            _lastStepReason = null;
        }

        var tool = FirstString(partValue, "tool", "name") ?? "tool";
        var toolId = FirstString(partValue, "callID", "id") ?? "tool-" + tool;
        var paths = ExtractPaths(partValue, state.Value);
        var detail = ToolDetail(tool, state.Value, paths);

        var events = new List<EngineEvent>(2);
        if (_startedTools.Add(toolId))
        {
            events.Add(new EngineEvent("tool", Bound($"{tool} started: {detail}", MaxText)));
        }
        if (status is not ("completed" or "error") || !_finishedTools.Add(toolId))
        {
            return events;
        }
        var outcome = status == "completed" ? "completed" : "failed";
        var text = $"{tool} {outcome}: {detail}";
        var output = status == "completed"
            ? GetValue(state.Value, "output")
            : GetValue(state.Value, "error");
        if (output is { } outputValue)
        {
            text += " | " + Compact(outputValue);
        }
        events.Add(new EngineEvent("tool", Bound(text, MaxText)));
        return events;
    }

    private static string ToolDetail(string tool, JsonElement state, List<string> paths)
    {
        var input = GetObject(state, "input");
        if (input is { } inputValue)
        {
            var command = FirstString(inputValue, "command", "cmd");
            if (!string.IsNullOrEmpty(command))
            {
                return Bound(command, MaxToolOutput);
            }
        }
        if (paths.Count > 0)
        {
            return paths[0];
        }
        return tool;
    }

    private static List<string> ExtractPaths(JsonElement part, JsonElement state)
    {
        var paths = new List<string>();
        if (GetValue(state, "input") is { } input)
        {
            CollectPaths(input, paths);
            CollectPatchPaths(input, paths);
        }
        if (GetValue(state, "metadata") is { } metadata)
        {
            CollectPaths(metadata, paths);
        }
        if (GetValue(part, "metadata") is { } partMetadata)
        {
            CollectPaths(partMetadata, paths);
        }
        return paths;
    }

    private static bool CollectPatchPaths(JsonElement input, List<string> paths)
    {        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("patch", out var patch) ||
            patch.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        foreach (Match match in PatchFilePattern().Matches(patch.GetString() ?? ""))
        {
            var path = match.Groups[1].Value.Trim();
            if (path.Length > 0 && !paths.Contains(path))
            {
                paths.Add(path);
            }
        }
        return true;
    }

    private static void CollectPaths(JsonElement value, List<string> paths, string? key = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (key is not null && PathKeys.Contains(key))
                {
                    var path = value.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(path) && !paths.Contains(path))
                    {
                        paths.Add(path);
                    }
                }
                return;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    CollectPaths(item, paths, key);
                }
                return;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    var name = property.Name.Trim().ToLowerInvariant().Replace('-', '_');
                    CollectPaths(property.Value, paths, name);
                }
                return;
        }
    }

    /// <summary>
    /// Reads one part's own usage contribution: input plus cache read/write, and
    /// output plus reasoning. A part ID contributes once; identical replays are ignored and
    /// revised payloads update that contribution. Components that
    /// were not reported stay null.
    /// </summary>
    private bool TryReadUsage(JsonElement? tokens, string? partId, out long? inputTokens, out long? outputTokens)
    {
        inputTokens = null;
        outputTokens = null;
        if (tokens is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (string.IsNullOrEmpty(partId))
        {
            // No stable phase identity means no safe deduplication or aggregation.
            return false;
        }
        _usageSeen.TryGetValue(partId, out var previous);
        var cache = GetValue(value, "cache");
        var input = FirstTokenNumber(value, "input", "input_tokens");
        var output = FirstTokenNumber(value, "output", "output_tokens");
        var reasoning = FirstTokenNumber(value, "reasoning", "reasoning_tokens", "reasoning_output_tokens");
        var cacheRead = CacheNumber(cache, value, "read", "cache_read", "cached_input_tokens");
        var cacheWrite = CacheNumber(cache, value, "write", "cache_write");
        var current = new UsageComponents(input ?? previous?.Input, output ?? previous?.Output,
            reasoning ?? previous?.Reasoning, cacheRead ?? previous?.CacheRead, cacheWrite ?? previous?.CacheWrite);
        if (current == previous) return false;
        _usageSeen[partId] = current;
        inputTokens = SumKnown(current.Input, current.CacheRead, current.CacheWrite);
        outputTokens = SumKnown(current.Output, current.Reasoning);
        return inputTokens is not null || outputTokens is not null;
    }

    private static double? CacheNumber(JsonElement? cache, JsonElement fallback, params string[] keys)
    {
        var value = cache is { } cacheValue ? FirstTokenNumber(cacheValue, keys) : null;
        value ??= FirstTokenNumber(fallback, keys);
        return value;
    }

    private static long? SumKnown(params double?[] components)
    {
        var total = 0d;
        var reported = false;
        foreach (var component in components)
        {
            if (component is { } value)
            {
                total += value;
                reported = true;
            }
        }
        return reported ? (long)Math.Round(total) : null;
    }

    private static double? FirstTokenNumber(JsonElement container, params string[] keys)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var key in keys)
        {
            if (!container.TryGetProperty(key, out var property))
            {
                continue;
            }
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number) &&
                double.IsFinite(number) && number >= 0)
            {
                return number;
            }
        }
        return null;
    }

    private void RememberSession(JsonElement value, JsonElement? properties, JsonElement? part)
    {
        if (SessionId is not null)
        {
            return;
        }
        SessionId = FirstString(value, "sessionID", "session_id", "sessionId")
            ?? (properties is { } p ? FirstString(p, "sessionID", "session_id") : null)
            ?? (part is { } pt ? FirstString(pt, "sessionID", "session_id") : null);
    }

    private static string? FirstString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                var text = property.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }
        return null;
    }

    private static string? GetString(JsonElement value, string name) => FirstString(value, name);

    private static JsonElement? GetValue(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property))
        {
            return property;
        }
        return null;
    }

    private static JsonElement? GetObject(JsonElement value, string name)
    {
        var property = GetValue(value, name);
        return property is { ValueKind: JsonValueKind.Object } ? property : null;
    }

    private static string Normalize(string? value) =>
        value is null ? "" : value.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_');

    public static string Bound(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + "…";

    private static string Compact(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Bound(value.GetString() ?? "", MaxToolOutput),
        JsonValueKind.Object => CompactObject(value, 0),
        JsonValueKind.Array => CompactArray(value, 0),
        _ => value.GetRawText(),
    };

    private static string CompactObject(JsonElement value, int depth)
    {
        if (depth >= 4)
        {
            return "[truncated]";
        }
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var property in value.EnumerateObject())
        {
            if (builder.Length > MaxToolOutput)
            {
                builder.Append("…");
                break;
            }
            if (!first)
            {
                builder.Append(',');
            }
            first = false;
            builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
            builder.Append(CompactValue(property.Value, depth + 1));
        }
        return builder.Append('}').ToString();
    }

    private static string CompactArray(JsonElement value, int depth)
    {
        if (depth >= 4)
        {
            return "[truncated]";
        }
        var builder = new StringBuilder("[");
        var first = true;
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (count++ >= 64)
            {
                builder.Append("[truncated]");
                break;
            }
            if (!first)
            {
                builder.Append(',');
            }
            first = false;
            builder.Append(CompactValue(item, depth + 1));
        }
        return builder.Append(']').ToString();
    }

    private static string CompactValue(JsonElement value, int depth) => value.ValueKind switch
    {
        JsonValueKind.String => Bound(value.GetString() ?? "", MaxToolOutput),
        JsonValueKind.Object => CompactObject(value, depth),
        JsonValueKind.Array => CompactArray(value, depth),
        _ => value.GetRawText(),
    };

    [GeneratedRegex(@"^\*\*\*\s+(?:Update|Add|Delete)\s+File:\s*(\S.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PatchFilePattern();
}
