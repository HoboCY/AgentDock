using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace AgentDock.Engine.Tests;

public sealed class GoRelayTests
{
    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false });

    [Fact]
    public async Task OnlyFixedAuthenticatedRouteAndModelAreForwarded()
    {
        var forwarded = new List<(Uri Url, string Authorization, string Session, string Body)>();
        var handler = new MockHandler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            forwarded.Add((request.RequestUri!, request.Headers.Authorization?.ToString() ?? "",
                request.Headers.TryGetValues("x-opencode-session", out var sessions) ? string.Join(",", sessions) : "",
                body));
            return MockHandler.Response("data: [DONE]\n\n", "text/event-stream");
        });
        await using var relay = await GoRelay.StartAsync("private-test-key", "job-fixture", handler);
        using var client = Client();

        var unauthenticated = await client.PostAsync(relay.Url + "/chat/completions",
            new StringContent("""{"model":"deepseek-v4.1-flash"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, unauthenticated.StatusCode);

        using var wrongRoute = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/responses")
        {
            Content = new StringContent("""{"model":"deepseek-v4.1-flash"}"""),
        };
        wrongRoute.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongRoute)).StatusCode);

        using var wrongModel = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/chat/completions")
        {
            Content = new StringContent("""{"model":"other"}"""),
        };
        wrongModel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(wrongModel)).StatusCode);

        using var success = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/chat/completions")
        {
            Content = new StringContent("""{"model":"deepseek-v4.1-flash","messages":[]}"""),
        };
        success.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        success.Headers.TryAddWithoutValidation("x-opencode-session", "session-123");
        var response = await client.SendAsync(success);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var captured = Assert.Single(forwarded);
        Assert.Equal("https://opencode.ai/zen/go/v1/chat/completions", captured.Url.ToString());
        Assert.Equal("Bearer private-test-key", captured.Authorization);
        Assert.Equal("session-123", captured.Session);
        Assert.Contains("deepseek-v4.1-flash", captured.Body);
        Assert.Equal(1, relay.LatestMeasurement.Sequence); // Rejected requests are not generation phases.
    }

    [Fact]
    public async Task RedirectsAreNotFollowedAndBecomeBadGateway()
    {
        var handler = new MockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://invalid.example/");
            return Task.FromResult(response);
        });
        await using var relay = await GoRelay.StartAsync("private-test-key", "job-fixture", handler);
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/chat/completions")
        {
            Content = new StringContent("""{"model":"deepseek-v4.1-flash"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, relay.LatestMeasurement.Sequence);
    }

    [Fact]
    public async Task FailedUpstreamAttemptDoesNotConsumeAGenerationPhase()
    {
        var handler = new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        { Content = new StringContent("temporary upstream failure") }));
        await using var relay = await GoRelay.StartAsync("fixture-key", "job-fixture", handler);
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/chat/completions")
        { Content = new StringContent("""{"model":"deepseek-v4.1-flash"}""") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, relay.LatestMeasurement.Sequence);
    }

    [Fact]
    public async Task OversizedBodiesAreRejected()
    {
        var handler = new MockHandler((_, _) => Task.FromResult(MockHandler.Response("{}", "application/json")));
        await using var relay = await GoRelay.StartAsync("private-test-key", "job-fixture", handler);
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/chat/completions")
        {
            Content = new ByteArrayContent(new byte[12 * 1024 * 1024 + 1]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task GenerationTimingComesFromStreamedDeltasUntilUsage()
    {
        var chunks = new[]
        {
            """data: {"choices":[{"delta":{"role":"assistant"}}]}""" + "\n\n",
            """data: {"choices":[{"delta":{"reasoning_content":"think"}}]}""" + "\n\n",
            """data: {"choices":[{"delta":{"content":"answer"}}]}""" + "\n\n",
            """data: {"choices":[{"delta":{}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""" + "\n\n",
            "data: [DONE]\n\n",
        };
        var handler = new MockHandler((_, _) =>
            Task.FromResult(MockHandler.Response(new ChunkedContent(chunks, 40))));
        await using var relay = await GoRelay.StartAsync("private-test-key", "job-fixture", handler);
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/chat/completions")
        {
            Content = new StringContent("""{"model":"deepseek-v4.1-flash"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("[DONE]", body);
        var measurement = relay.LatestMeasurement;
        Assert.Equal(1, measurement.Sequence);
        Assert.NotNull(measurement.Seconds);
        Assert.InRange(measurement.Seconds!.Value, 0.02, 10);
    }

    [Fact]
    public async Task UnmeasurableToolOnlyStreamsPublishNullSeconds()
    {
        var chunks = new[]
        {
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"bash"}}]}}]}""" + "\n\n",
            """data: {"choices":[{"delta":{}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""" + "\n\n",
            "data: [DONE]\n\n",
        };
        var handler = new MockHandler((_, _) =>
            Task.FromResult(MockHandler.Response(new ChunkedContent(chunks, 10))));
        await using var relay = await GoRelay.StartAsync("private-test-key", "job-fixture", handler);
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, relay.Url + "/chat/completions")
        {
            Content = new StringContent("""{"model":"deepseek-v4.1-flash"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        var response = await client.SendAsync(request);
        await response.Content.ReadAsStringAsync();

        var measurement = relay.LatestMeasurement;
        Assert.Equal(1, measurement.Sequence);
        Assert.Null(measurement.Seconds);
    }

    [Fact]
    public async Task RequestsAreNeverLoggedButRejectedWithoutCapability()
    {
        var handler = new MockHandler((_, _) => Task.FromResult(MockHandler.Response("{}", "application/json")));
        await using var relay = await GoRelay.StartAsync("secret-fixture-value", "job-fixture", handler);
        using var client = Client();
        var response = await client.PostAsync(relay.Url + "/chat/completions",
            new StringContent("""{"model":"deepseek-v4.1-flash"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-fixture-value", text);
    }
}

internal sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);

    public static HttpResponseMessage Response(string content, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, mediaType),
    };

    public static HttpResponseMessage Response(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
}

internal sealed class ChunkedContent(string[] chunks, int delayMilliseconds) : HttpContent
{
    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new ChunkedStream(chunks, delayMilliseconds));

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        foreach (var chunk in chunks)
        {
            await Task.Delay(delayMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(chunk);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class ChunkedStream(string[] chunks, int delayMilliseconds) : Stream
    {
        private int _index;
        private int _offset;

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
            if (_index >= chunks.Length || buffer.Length == 0)
            {
                return 0;
            }
            if (_offset == 0) await Task.Delay(delayMilliseconds, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(chunks[_index]);
            var count = Math.Min(bytes.Length - _offset, buffer.Length);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == bytes.Length) { _index++; _offset = 0; }
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
