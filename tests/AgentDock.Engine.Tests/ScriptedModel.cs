using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentDock.Engine.Tests;

/// <summary>Scripted chat-completions reply: a tool call or a final text answer.</summary>
internal sealed class ScriptedModel
{
    private readonly object[] _script;

    public ScriptedModel(params object[] script)
    {
        _script = script;
    }

    public object Reply(int requestIndex) => _script[Math.Min(requestIndex, _script.Length - 1)];

    public static object ToolCall(string name, object arguments) => new { Kind = "tool", Name = name, Arguments = arguments };

    public static object Text(string content) => new { Kind = "text", Content = content };
}

/// <summary>
/// Local fake OpenCode Go upstream: pinned model, SSE streaming, usage chunk and
/// [DONE]. No real credential and no remote request are involved.
/// </summary>
internal sealed class ScriptedHandler(ScriptedModel model) : HttpMessageHandler
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _requests) - 1;
        var reply = model.Reply(index);
        var kind = (string)reply.GetType().GetProperty("Kind")!.GetValue(reply)!;
        string sse;
        if (kind == "tool")
        {
            var name = (string)reply.GetType().GetProperty("Name")!.GetValue(reply)!;
            var arguments = JsonSerializer.Serialize(reply.GetType().GetProperty("Arguments")!.GetValue(reply));
            sse = SseStream(
                Chunk(new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?> { ["role"] = "assistant", ["reasoning_content"] = "fixture reasoning" },
                }),
                Chunk(new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["tool_calls"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["index"] = 0,
                                ["id"] = "call_fixture",
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?> { ["name"] = name, ["arguments"] = arguments },
                            },
                        },
                    },
                }),
                Chunk(new Dictionary<string, object?> { ["delta"] = new Dictionary<string, object?>() },
                    finishReason: "tool_calls", usage: Usage()));
        }
        else
        {
            var content = (string)reply.GetType().GetProperty("Content")!.GetValue(reply)!;
            sse = SseStream(
                Chunk(new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?> { ["role"] = "assistant", ["reasoning_content"] = "fixture reasoning" },
                }),
                Chunk(new Dictionary<string, object?> { ["delta"] = new Dictionary<string, object?> { ["content"] = content } }),
                Chunk(new Dictionary<string, object?> { ["delta"] = new Dictionary<string, object?>() },
                    finishReason: "stop", usage: Usage()));
        }
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            // Stream the SSE frames with small gaps so generation timing is real.
            Content = new DelayedContent(sse, 30),
        };
        return Task.FromResult(response);
    }

    private static Dictionary<string, object?> Usage() => new()
    {
        ["prompt_tokens"] = 10,
        ["completion_tokens"] = 2,
        ["total_tokens"] = 12,
    };

    private static string Chunk(Dictionary<string, object?> fields, string? finishReason = null,
        Dictionary<string, object?>? usage = null)
    {
        var choice = new Dictionary<string, object?>(fields)
        {
            ["index"] = 0,
            ["finish_reason"] = finishReason is null ? null : finishReason,
        };
        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new object[] { choice },
        };
        if (usage is not null)
        {
            payload["usage"] = usage;
        }
        return JsonSerializer.Serialize(payload);
    }

    private static string SseStream(params string[] events)
    {
        var builder = new StringBuilder();
        foreach (var data in events)
        {
            builder.Append("data: ").Append(data).Append("\n\n");
        }
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }
}

/// <summary>SSE content that is handed out one frame at a time with a delay.</summary>
internal sealed class DelayedContent(string text, int delayMilliseconds) : HttpContent
{
    private readonly string[] _frames = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(frame => frame + "\n\n")
        .ToArray();

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new DelayedStream(_frames, delayMilliseconds));

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        foreach (var frame in _frames)
        {
            await Task.Delay(delayMilliseconds);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(frame));
            await stream.FlushAsync();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class DelayedStream(string[] frames, int delayMilliseconds) : Stream
    {
        private int _index;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= frames.Length)
            {
                return 0;
            }
            await Task.Delay(delayMilliseconds, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(frames[_index++]);
            var copy = Math.Min(bytes.Length, buffer.Length);
            bytes.AsMemory(0, copy).CopyTo(buffer);
            return copy;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
