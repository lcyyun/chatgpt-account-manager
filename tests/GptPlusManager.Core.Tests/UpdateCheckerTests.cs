using System.Net;
using System.Text;
using GptPlusManager.Core.Network;

namespace GptPlusManager.Core.Tests;

/// <summary>
/// 锁定版本比较与 GitHub 响应解析。失败路径必须"给一句人话 + 不抛异常"——
/// 国内到 GitHub 的连接经常不稳，检查更新崩掉界面是不可接受的。
/// </summary>
public sealed class UpdateCheckerTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond());
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static UpdateChecker Checker(FakeHandler handler) => new(new HttpClient(handler));

    private static string Release(string tag, string url = "https://github.com/x/y/releases/tag/1") =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","published_at":"2026-10-28T12:00:00Z"}""";

    // ---- 版本比较 ----

    [Theory]
    [InlineData("v0.7.1", "0.7.1")]
    [InlineData("0.7.1", "0.7.1")]
    [InlineData("V1.0", "1.0")]
    [InlineData("0.7.1+abc123", "0.7.1")]
    public void NormalizeVersion_StripsPrefixAndMetadata(string input, string expected)
    {
        Assert.Equal(expected, UpdateChecker.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("0.7.1", "0.8.0")]
    [InlineData("0.7.0", "0.7.1")]
    [InlineData("0.7.1", "0.7.1.1")]
    [InlineData("0.9.9", "1.0.0")]
    public void Compare_OlderIsLessThanNewer(string older, string newer)
    {
        Assert.True(UpdateChecker.TryParseVersion(older, out var a));
        Assert.True(UpdateChecker.TryParseVersion(newer, out var b));
        Assert.True(UpdateChecker.Compare(a, b) < 0);
    }

    // ---- 响应解析 ----

    [Fact]
    public async Task UpdateAvailable_WhenLatestTagIsNewer()
    {
        var checker = Checker(new FakeHandler(() => Json(Release("v0.8.0"))));

        var result = await checker.CheckAsync("0.7.1");

        Assert.True(result.IsSuccess);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.8.0", result.LatestVersion);
        Assert.Equal("0.7.1", result.CurrentVersion);
        Assert.Equal("https://github.com/x/y/releases/tag/1", result.ReleaseUrl);
        Assert.NotNull(result.PublishedAt);
    }

    [Fact]
    public async Task UpToDate_WhenVersionsEqual()
    {
        var checker = Checker(new FakeHandler(() => Json(Release("v0.7.1"))));

        var result = await checker.CheckAsync("0.7.1.0");

        Assert.True(result.IsSuccess);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task UpToDate_WhenCurrentIsAheadOfLatestTag()
    {
        // 本地开发版高于最新 release 时不打扰用户。
        var checker = Checker(new FakeHandler(() => Json(Release("v0.7.0"))));

        var result = await checker.CheckAsync("0.7.1");

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task RateLimited_GivesHumanMessage()
    {
        var checker = Checker(new FakeHandler(() =>
            Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden)));

        var result = await checker.CheckAsync("0.7.1");

        Assert.False(result.IsSuccess);
        Assert.Contains("限流", result.Error);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotThrow()
    {
        var checker = Checker(new FakeHandler(() => throw new HttpRequestException("connection reset")));

        var result = await checker.CheckAsync("0.7.1");

        Assert.False(result.IsSuccess);
        Assert.Contains("无法连接 GitHub", result.Error);
    }

    [Fact]
    public async Task MalformedJson_GivesErrorInsteadOfThrowing()
    {
        var checker = Checker(new FakeHandler(() => Json("<html>not json</html>")));

        var result = await checker.CheckAsync("0.7.1");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task MissingTag_NameIsReported()
    {
        var checker = Checker(new FakeHandler(() => Json("""{"html_url":"x"}""")));

        var result = await checker.CheckAsync("0.7.1");

        Assert.False(result.IsSuccess);
        Assert.Contains("tag_name", result.Error);
    }
}
