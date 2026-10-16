using System.Net;
using AgentDock.Desktop;
using Xunit;

namespace AgentDock.Tests;

public sealed class ReleaseCatalogTests
{
    [Theory]
    [InlineData("v0.2.0", true)]
    [InlineData("v0.1.0", false)]
    [InlineData("v0.0.9", false)]
    [InlineData("https://untrusted.invalid/install.exe", false)]
    public async Task CheckOnlyReportsAComparableNewerRelease(string tag, bool expected)
    {
        using var http = new HttpClient(new Response(HttpStatusCode.OK, "{\"tag_name\":\"" + tag + "\"}"));
        Assert.Equal(expected, (await ReleaseCatalog.CheckAsync(http, "0.1.0")).NewVersion);
    }

    [Fact]
    public async Task UnpublishedRepositoryReportsNoRelease()
    {
        using var http = new HttpClient(new Response(HttpStatusCode.NotFound, "{}"));
        var notice = await ReleaseCatalog.CheckAsync(http, "0.1.0");
        Assert.False(notice.NewVersion);
        Assert.Contains("尚无公开", notice.Message);
    }

    private sealed class Response(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }
}
