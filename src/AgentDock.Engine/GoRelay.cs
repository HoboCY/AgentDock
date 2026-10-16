using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentDock.Engine;

/// <summary>
/// Per-job loopback capability relay in front of the fixed OpenCode Go endpoint.
///
/// Only the AgentDock process holds the real key. The sandboxed OpenCode gets a
/// random per-job token that works only for POST /v1/chat/completions on
/// 127.0.0.1 with this relay's Host header, only for the pinned model, and only
/// until the job ends. Redirects are rejected instead of forwarding the key.
/// Request bodies, credentials and headers are never logged.
/// </summary>
internal sealed class GoRelay : IAsyncDisposable
{
    public const string Model = "deepseek-v4.1-flash";
    public const string Provider = "opencode-go";
    public const int OutputTokenMax = 131_072;
    public const string Upstream = "https://opencode.ai/zen/go/v1/chat/completions";

    private const int MaxRequestBytes = 12 * 1024 * 1024;
    private const string Route = "/v1/chat/completions";

    private readonly string _key;
    private readonly string _jobId;
    private readonly string _token;
    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly string _expectedHost;
    private readonly object _measureGate = new();
    private int _closed;
    private long _measurementSequence;
    private double? _measurementSeconds;

    private GoRelay(string key, string jobId, WebApplication app, HttpClient client, string token, int port)
    {
        _key = key;
        _jobId = jobId;
        _app = app;
        _client = client;
        _token = token;
        _expectedHost = $"127.0.0.1:{port}";
        Url = $"http://127.0.0.1:{port}/v1";
    }

    /// <summary>Base URL handed to OpenCode (it appends /chat/completions).</summary>
    public string Url { get; }

    public string Token => _token;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Latest successfully completed upstream response. Rejected and failed
    /// attempts are not model phases; a successful but unmeasurable phase has null seconds.
    /// </summary>
    public (long Sequence, double? Seconds) LatestMeasurement
    {
        get
        {
            lock (_measureGate)
            {
                return (_measurementSequence, _measurementSeconds);
            }
        }
    }

    public static async Task<GoRelay> StartAsync(string key, string jobId, HttpMessageHandler? transport = null,
        CancellationToken cancellationToken = default)
    {
        CredentialVault.Validate(key);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0);
            options.AddServerHeader = false;
        });
        var app = builder.Build();
        var client = CreateClient(transport);
        var port = 0;
        GoRelay? relay = null;
        app.MapPost(Route, (HttpContext context) => relay!.HandleAsync(context));
        app.MapFallback((HttpContext context) =>
            RejectAsync(context, StatusCodes.Status403Forbidden, "Invalid job capability or route", context.RequestAborted));
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.FirstOrDefault(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
            if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException("The loopback relay did not report a bound address.");
            }
            port = uri.Port;
            relay = new GoRelay(key, jobId, app, client, token, port);
            return relay;
        }
        catch
        {
            client.Dispose();
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler? transport)
    {
        var handler = transport ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async Task HandleAsync(HttpContext context)
    {
        var cancellation = context.RequestAborted;
        if (!Authorized(context))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "Invalid job capability or route", cancellation).ConfigureAwait(false);
            return;
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(context, cancellation).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            await RejectAsync(context, StatusCodes.Status413PayloadTooLarge, "Invalid request size", cancellation).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        string? model = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("model", out var modelElement) &&
                modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString();
            }
        }
        catch (JsonException)
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest, "Invalid JSON request", cancellation).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(model, Model, StringComparison.Ordinal))
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest, "This job is pinned to " + Model, cancellation).ConfigureAwait(false);
            return;
        }

        var timer = new GenerationTimer();
        var started = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Upstream) { Content = new ByteArrayContent(body) };
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
            request.Headers.TryAddWithoutValidation("Accept", FirstHeader(context, "Accept") ?? "text/event-stream");
            request.Headers.TryAddWithoutValidation("User-Agent", "opencode/1.18.30 agentdock/1");
            request.Headers.TryAddWithoutValidation("x-opencode-session", _jobId);
            foreach (var name in new[] { "x-opencode-session", "x-opencode-request", "x-opencode-project", "x-opencode-client" })
            {
                var value = FirstHeader(context, name);
                if (value is not null)
                {
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                await RejectAsync(context, StatusCodes.Status502BadGateway,
                    "OpenCode Go returned an unexpected redirect", cancellation).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            started = true;
            await using var upstream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                while (Volatile.Read(ref _closed) == 0)
                {
                    var read = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    // Parse before the client can observe the final chunk, so the
                    // generation measurement is set when the step finishes.
                    timer.Observe(buffer.AsSpan(0, read));
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                }
                if (response.IsSuccessStatusCode)
                    PublishMeasurement(timer.TryFinish(out var seconds) ? seconds : null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is HttpRequestException or IOException or ObjectDisposedException)
        {
            if (!started)
            {
                await RejectAsync(context, StatusCodes.Status502BadGateway,
                    "OpenCode Go connection failed; check network and subscription", CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                context.Abort();
            }
        }
    }

    private bool Authorized(HttpContext context)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return false;
        }
        var host = context.Request.Headers.Host.ToString();
        if (!string.Equals(host, _expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var presented = context.Request.Headers.Authorization.ToString();
        return FixedTimeEquals(presented, "Bearer " + _token);
    }

    private static bool FixedTimeEquals(string presented, string expected)
    {
        var left = Encoding.UTF8.GetBytes(presented);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string? FirstHeader(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        return values.Count == 0 ? null : values[0];
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var declared = context.Request.ContentLength;
        if (declared is > MaxRequestBytes)
        {
            throw new BodyTooLargeException();
        }
        using var stream = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                if (stream.Length + read > MaxRequestBytes)
                {
                    throw new BodyTooLargeException();
                }
                stream.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return stream.ToArray();
    }

    private static async Task RejectAsync(HttpContext context, int status, string message, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { error = new { message } });
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = payload.Length;
        try
        {
            await context.Response.Body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client went away; nothing else to do.
        }
    }

    private void PublishMeasurement(double? seconds)
    {
        lock (_measureGate)
        {
            _measurementSequence++;
            _measurementSeconds = seconds;
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // Shutdown is best effort; disposal below still releases the port.
        }
        await _app.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    private sealed class BodyTooLargeException : Exception;
}
