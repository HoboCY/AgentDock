using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentDock.Engine;

/// <summary>
/// Monotonic generation-phase timer fed only by actual upstream SSE events. SSE
/// payloads are buffered until an event boundary (LF or CRLF) and parsed as JSON
/// with whitespace tolerance, so events split across chunks are still measured.
/// The window opens at the first non-empty output/reasoning delta and closes at
/// a usage chunk (or the stream terminator) that arrives after the window was
/// already open; a chunk that carries both the first delta and usage cannot
/// close it, so an unmeasurable single-chunk generation stays null instead of
/// publishing a fabricated zero duration. Tool-call deltas never open the
/// window, and queue wait plus tool execution happen outside the observed
/// stream, so neither is counted. A duration is published only when the stream
/// closed an open window; otherwise the speed stays nullable.
/// </summary>
internal sealed class GenerationTimer
{
    private const int MaxPendingBytes = 2 * 1024 * 1024;
    private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

    private readonly List<byte> _pending = [];
    private long _startTicks;
    private long _endTicks;
    private long _observedAtTicks;
    private bool _startWasUsageChunk;

    public bool Started => Volatile.Read(ref _startTicks) != 0;

    public void Observe(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }
        _observedAtTicks = Stopwatch.GetTimestamp();
        foreach (var value in chunk)
        {
            _pending.Add(value);
        }
        ProcessPending(final: false);
        if (_pending.Count > MaxPendingBytes)
        {
            _pending.Clear();
        }
    }

    /// <summary>Finalizes the measurement after the stream ended.</summary>
    public bool TryFinish(out double seconds)
    {
        ProcessPending(final: true);
        var start = Volatile.Read(ref _startTicks);
        var end = Volatile.Read(ref _endTicks);
        if (start != 0 && end != 0 && end > start)
        {
            seconds = (end - start) / (double)Stopwatch.Frequency;
            return true;
        }
        seconds = 0;
        return false;
    }

    private void ProcessPending(bool final)
    {
        var buffer = CollectionsMarshal.AsSpan(_pending);
        var consumed = 0;
        while (true)
        {
            var boundary = FindBoundary(buffer, consumed);
            if (boundary < 0)
            {
                break;
            }
            HandleBlock(buffer.Slice(consumed, boundary - consumed));
            consumed = boundary + 2;
        }
        if (final)
        {
            if (consumed < buffer.Length)
            {
                HandleBlock(buffer.Slice(consumed));
            }
            _pending.Clear();
            return;
        }
        if (consumed > 0)
        {
            _pending.RemoveRange(0, consumed);
        }
    }

    private static int FindBoundary(ReadOnlySpan<byte> buffer, int from)
    {
        for (var index = from; index < buffer.Length; index++)
        {
            if (buffer[index] != (byte)'\n')
            {
                continue;
            }
            if (index + 1 < buffer.Length && buffer[index + 1] == (byte)'\n')
            {
                return index;
            }
            if (index + 2 < buffer.Length && buffer[index + 1] == (byte)'\r' && buffer[index + 2] == (byte)'\n')
            {
                return index;
            }
        }
        return -1;
    }

    private void HandleBlock(ReadOnlySpan<byte> block)
    {
        var start = 0;
        for (var index = 0; index <= block.Length; index++)
        {
            if (index < block.Length && block[index] != (byte)'\n')
            {
                continue;
            }
            var line = Trim(block.Slice(start, index - start));
            start = index + 1;
            if (line.IsEmpty || !line.StartsWith("data:"u8))
            {
                continue;
            }
            var payload = Trim(line[5..]);
            if (payload.IsEmpty)
            {
                continue;
            }
            if (payload.SequenceEqual(DoneMarker))
            {
                // [DONE] may only close a window that a previous chunk opened.
                if (Started && !_startWasUsageChunk)
                {
                    MarkEnd();
                }
                continue;
            }
            HandlePayload(payload);
        }
    }

    /// <summary>
    /// Parses one SSE data payload as JSON. Content/reasoning deltas open the
    /// window; a usage object closes it only when the window was already open
    /// before this payload (so content+usage in the same chunk cannot produce a
    /// zero-length measurement). Tool calls and malformed payloads are ignored.
    /// </summary>
    private void HandlePayload(ReadOnlySpan<byte> payload)
    {
        JsonDocument document;
        try
        {
            // JsonDocument has no span overload; event payloads are bounded lines.
            document = JsonDocument.Parse(payload.ToArray());
        }
        catch (JsonException)
        {
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var startedBefore = Started;
            if (HasNonEmptyDelta(root) && !startedBefore)
            {
                MarkStart(hasUsage: HasUsage(root));
            }
            if (HasUsage(root) && startedBefore)
            {
                MarkEnd();
            }
        }
    }

    private void MarkStart(bool hasUsage)
    {
        if (Interlocked.CompareExchange(ref _startTicks, _observedAtTicks, 0) == 0)
        {
            _startWasUsageChunk = hasUsage;
        }
    }

    private void MarkEnd() => Interlocked.CompareExchange(ref _endTicks, _observedAtTicks, 0);

    private static bool HasUsage(JsonElement root) =>
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;

    private static bool HasNonEmptyDelta(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object ||
                !choice.TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (HasNonEmptyString(delta, "content") ||
                HasNonEmptyString(delta, "reasoning_content") ||
                HasNonEmptyString(delta, "reasoning"))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasNonEmptyString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(property.GetString());

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && (value[start] == (byte)' ' || value[start] == (byte)'\r' || value[start] == (byte)'\t'))
        {
            start++;
        }
        while (end >= start && (value[end] == (byte)'\r' || value[end] == (byte)' ' || value[end] == (byte)'\t'))
        {
            end--;
        }
        return value.Slice(start, end - start + 1);
    }
}
