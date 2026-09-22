using System.Net;
using System.Text;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Core.Tests;

/// <summary>
/// 锁定自检必须模仿 Codex 的真实请求形态。
///
/// <para>回归来源：最初的自检把工具放在顶层 <c>tools</c> 字段，而 Codex 实际是把工具
/// 作为 <c>input</c> 里的 <c>additional_tools</c> 项传递，并附带
/// <c>x-openai-internal-codex-responses-lite</c> 请求头。形态不对，端点会拒绝，
/// 于是一个完全能用的配置被自检判成"协议不兼容"——误报比不报更糟。</para>
/// </summary>
public sealed class ProviderConnectionTesterTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
        public List<(string Url, string Body, string? Auth, string? LiteHeader)> Calls { get; } = [];

        public Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Calls.Add((
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-openai-internal-codex-responses-lite", out var v)
                    ? string.Join(",", v)
                    : null));

            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ProviderConnectionTester Tester(Recorder recorder) =>
        new(new HttpClient(recorder));

    [Fact]
    public async Task SendsToolsInsideInputAsAdditionalTools_NotTopLevel()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1", usesResponsesLite: false);

        var post = recorder.Calls.Single(c => c.Body.Contains("additional_tools"));
        Assert.Contains("additional_tools", post.Body);

        // 顶层 tools 会被端点拒绝（实测），绝不能出现。
        var root = System.Text.Json.Nodes.JsonNode.Parse(post.Body)!.AsObject();
        Assert.Null(root["tools"]);

        // 两个 POST 都不该带顶层 tools，包括那个只有模型名的探测请求。
        foreach (var call in recorder.Calls.Where(c => c.Body.Contains("\"model\"")))
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(call.Body)!.AsObject();
            Assert.Null(node["tools"]);
        }
    }

    [Fact]
    public async Task SendsResponsesLiteHeader_WhenEnabled()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1", usesResponsesLite: true);

        // 每个请求都要带，包括探测性的 GET。
        Assert.All(recorder.Calls, call => Assert.Equal("true", call.LiteHeader));
    }

    [Fact]
    public async Task OmitsResponsesLiteHeader_WhenDisabled()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1", usesResponsesLite: false);

        Assert.All(recorder.Calls, call => Assert.Null(call.LiteHeader));
    }

    [Fact]
    public async Task HappyPath_ReportsThreePassingSteps()
    {
        var recorder = new Recorder((request, _) => request.Method == HttpMethod.Get
            ? Json("""{"data":[{"id":"m-1"}]}""")
            : Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        var report = await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1", true);

        Assert.True(report.Success);
        Assert.Equal(3, report.Steps.Count);
        Assert.All(report.Steps, step => Assert.True(step.Passed));
    }

    [Fact]
    public async Task MissingModel_FailsAtModelStep_NotToolStep()
    {
        var recorder = new Recorder((request, body) =>
            request.Method == HttpMethod.Get
                ? Json("""{"data":[{"id":"m-1"}]}""")
                : body.Contains("m-1")
                    ? Json("""{"id":"resp_1"}""")
                    : Json("""{"error":{"code":"400","message":"Unsupported model nope"}}""", HttpStatusCode.BadRequest));
        var tester = Tester(recorder);

        var report = await tester.TestAsync("https://api.example.com/v1", "sk-x", "nope", true);

        Assert.False(report.Success);
        Assert.Contains("模型", report.Steps[^1].Name);
        Assert.Contains("模型 ID", report.Diagnosis);
    }

    [Fact]
    public async Task BadKey_FailsAtConnectivityStep_WithKeyDiagnosis()
    {
        var recorder = new Recorder((_, _) =>
            Json("""{"error":{"message":"Invalid API Key"}}""", HttpStatusCode.Unauthorized));
        var tester = Tester(recorder);

        var report = await tester.TestAsync("https://api.example.com/v1", "bad", "m-1", true);

        Assert.False(report.Success);
        Assert.Contains("密钥", report.Diagnosis);
    }

    [Fact]
    public async Task UnreachableEndpoint_ReportsConnectionFailure()
    {
        var recorder = new Recorder((_, _) => throw new HttpRequestException("no route to host"));
        var tester = Tester(recorder);

        var report = await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1", true);

        Assert.False(report.Success);
        Assert.Contains("连不上", report.Diagnosis);
    }

    [Fact]
    public async Task ToolStepFailure_BlamesProtocolNotConfiguration()
    {
        // 端点接受模型，但拒绝工具形态：这是端点实现问题，用户改配置解决不了。
        var recorder = new Recorder((request, body) =>
        {
            if (request.Method == HttpMethod.Get) return Json("""{"data":[{"id":"m-1"}]}""");
            return body.Contains("additional_tools")
                ? Json("""{"error":{"type":"unsupported_feature","message":"custom tools require lite mode"}}""",
                    HttpStatusCode.BadRequest)
                : Json("""{"id":"resp_1"}""");
        });
        var tester = Tester(recorder);

        var report = await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1", false);

        Assert.False(report.Success);
        Assert.Contains("工具调用", report.Steps[^1].Name);
        Assert.Contains("提供方", report.Diagnosis);
    }

    [Fact]
    public async Task BlankBaseUrl_IsRejectedBeforeAnyRequest()
    {
        var recorder = new Recorder((_, _) => Json("{}"));
        var tester = Tester(recorder);

        var report = await tester.TestAsync("  ", "sk-x", "m-1", true);

        Assert.False(report.Success);
        Assert.Empty(recorder.Calls);
        Assert.Contains("Base URL", report.Diagnosis);
    }
}
