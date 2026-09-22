using System.Net;
using System.Text;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Core.Tests;

public sealed class ProviderModelProbeTests
{
    /// <summary>Answers /models with a canned body, so parsing is tested without a network.</summary>
    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        public string? LastAuth { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            LastAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ProviderModelProbe Probe(string body, HttpStatusCode status = HttpStatusCode.OK, StubHandler? handler = null)
    {
        handler ??= new StubHandler(body, status);
        return new ProviderModelProbe(new HttpClient(handler));
    }

    [Fact]
    public async Task Parses_OpenAiStyle_DataArray()
    {
        var handler = new StubHandler("""
            {"object":"list","data":[
              {"id":"mimo-v2.6-pro","object":"model","owned_by":"xiaomi"},
              {"id":"mimo-v2.6-flash","object":"model","owned_by":"xiaomi"}]}
            """);
        var probe = Probe("", handler: handler);

        var result = await probe.ListModelsAsync("https://api.example.com/v1", "sk-x");

        Assert.True(result.Success);
        Assert.Equal(["mimo-v2.6-pro", "mimo-v2.6-flash"], result.Models);
        Assert.Equal("https://api.example.com/v1/models", handler.LastUrl);
        Assert.Equal("Bearer sk-x", handler.LastAuth);
    }

    [Fact]
    public async Task TrimsTrailingSlashOnBaseUrl()
    {
        var handler = new StubHandler("""{"data":[{"id":"a"}]}""");
        var probe = Probe("", handler: handler);

        await probe.ListModelsAsync("https://api.example.com/v1/", "k");

        Assert.Equal("https://api.example.com/v1/models", handler.LastUrl);
    }

    [Theory]
    [InlineData("""{"models":[{"id":"m1"}]}""")]
    [InlineData("""{"model_ids":["m1"]}""")]
    [InlineData("""["m1"]""")]
    [InlineData("""{"data":[{"name":"m1"}]}""")]
    [InlineData("""{"data":[{"model":"m1"}]}""")]
    public async Task AcceptsAlternativeShapes(string body)
    {
        // 中转站实现并不统一，解析要宽容，否则用户会被"获取失败"挡住。
        var result = await Probe(body).ListModelsAsync("https://x/v1", "k");

        Assert.True(result.Success);
        Assert.Equal(["m1"], result.Models);
    }

    [Fact]
    public async Task DeduplicatesRepeatedIds()
    {
        var result = await Probe("""{"data":[{"id":"a"},{"id":"a"},{"id":"b"}]}""")
            .ListModelsAsync("https://x/v1", "k");

        Assert.Equal(["a", "b"], result.Models);
    }

    [Fact]
    public async Task PreservesEndpointCasing()
    {
        // 大小写就是这里的全部意义：端点区分大小写，必须原样带出来。
        var result = await Probe("""{"data":[{"id":"MiMo-V2.6-Pro"}]}""")
            .ListModelsAsync("https://x/v1", "k");

        Assert.Equal("MiMo-V2.6-Pro", Assert.Single(result.Models));
    }

    [Fact]
    public async Task HttpError_ReportsStatusAndBody()
    {
        var result = await Probe("""{"error":"bad key"}""", HttpStatusCode.Unauthorized)
            .ListModelsAsync("https://x/v1", "k");

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.Contains("bad key", result.Error);
    }

    [Fact]
    public async Task EmptyList_IsFailureNotSilentSuccess()
    {
        var result = await Probe("""{"data":[]}""").ListModelsAsync("https://x/v1", "k");

        Assert.False(result.Success);
        Assert.Contains("手动填写", result.Error);
    }

    [Fact]
    public async Task MalformedJson_IsFailureNotThrow()
    {
        var result = await Probe("not json at all").ListModelsAsync("https://x/v1", "k");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BlankBaseUrl_IsRejectedBeforeAnyRequest()
    {
        var result = await Probe("""{"data":[{"id":"a"}]}""").ListModelsAsync("   ", "k");

        Assert.False(result.Success);
        Assert.Contains("Base URL", result.Error);
    }

    [Fact]
    public async Task NoApiKey_StillAttemptsWithoutAuthHeader()
    {
        // 有些自建端点不需要鉴权，不该因为没填 key 就不让查。
        var handler = new StubHandler("""{"data":[{"id":"a"}]}""");
        var probe = Probe("", handler: handler);

        var result = await probe.ListModelsAsync("https://x/v1", null);

        Assert.True(result.Success);
        Assert.Null(handler.LastAuth);
    }
}
