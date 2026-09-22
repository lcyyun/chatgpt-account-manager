using System.Net;
using System.Text;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Core.Tests;

/// <summary>
/// 锁定自检必须模仿 Codex 的真实请求形态。
///
/// <para>回归来源有两层。最初自检把工具放在顶层 <c>tools</c>，而 Code mode 下 Codex 实际是把
/// 工具作为 <c>input</c> 里的 <c>additional_tools</c> 项传递，并附带
/// <c>x-openai-internal-codex-responses-lite</c> 请求头——形态不对，端点会拒绝，
/// 于是一个完全能用的配置被自检判成"协议不兼容"，误报比不报更糟。</para>
///
/// <para>第二层：工具形态<b>不是固定的</b>，由供应商的实际能力决定。实测小米端点会拒绝
/// <c>custom</c> 工具与 <c>web_search</c>，而 DeepSeek 两者都接受。所以自检必须按
/// 配置裁剪，发错就等于替一个能用的配置报故障。</para>
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

    /// <summary>Classic 协议：工具发在顶层 <c>tools</c>，用标准 <c>function</c> 类型。</summary>
    [Fact]
    public async Task ClassicProtocol_SendsTopLevelFunctionTools()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1",
            usesResponsesLite: false, protocol: ToolProtocol.Classic);

        var post = recorder.Calls.Single(c => c.Body.Contains("\"tools\""));
        var root = System.Text.Json.Nodes.JsonNode.Parse(post.Body)!.AsObject();
        var tools = root["tools"]!.AsArray();

        Assert.Contains(tools, t => t!["type"]!.GetValue<string>() == "function");
        // Classic 下不该出现 code mode 的 additional_tools。
        Assert.DoesNotContain("additional_tools", post.Body);
    }

    /// <summary>Code mode 协议：工具作为 <c>input</c> 里的 <c>additional_tools</c>。</summary>
    [Fact]
    public async Task CodeModeProtocol_SendsToolsInsideInputAsAdditionalTools()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1",
            usesResponsesLite: true, protocol: ToolProtocol.CodeMode);

        var post = recorder.Calls.Single(c => c.Body.Contains("additional_tools"));
        Assert.Contains("additional_tools", post.Body);

        // 顶层 tools 在 code mode 下会被端点拒绝（实测），绝不能出现。
        var root = System.Text.Json.Nodes.JsonNode.Parse(post.Body)!.AsObject();
        Assert.Null(root["tools"]);
    }

    /// <summary>
    /// 端点不支持自定义工具时，请求里不能出现 <c>custom</c> —— 实测小米会以
    /// "custom tools require MiMo freeform Responses lite mode" 拒绝整个请求。
    /// </summary>
    [Fact]
    public async Task OmitsCustomTools_WhenEndpointLacksSupport()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1",
            usesResponsesLite: false, protocol: ToolProtocol.Classic, supportsCustomTools: false);

        var post = recorder.Calls.Single(c => c.Body.Contains("\"tools\""));
        Assert.DoesNotContain("\"custom\"", post.Body);
    }

    [Fact]
    public async Task IncludesCustomTools_WhenEndpointSupportsThem()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1",
            usesResponsesLite: false, protocol: ToolProtocol.Classic, supportsCustomTools: true);

        var post = recorder.Calls.Single(c => c.Body.Contains("\"tools\""));
        Assert.Contains("\"custom\"", post.Body);
    }

    /// <summary>
    /// 端点不支持联网搜索时不能带 <c>web_search</c> —— 实测小米会以
    /// "tool type 'web_search' is not supported by this gateway phase" 拒绝。
    /// </summary>
    [Fact]
    public async Task OmitsWebSearch_WhenEndpointLacksSupport()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1",
            usesResponsesLite: false, protocol: ToolProtocol.Classic, supportsWebSearch: false);

        var post = recorder.Calls.Single(c => c.Body.Contains("\"tools\""));
        Assert.DoesNotContain("web_search", post.Body);
    }

    [Fact]
    public async Task IncludesWebSearch_WhenEndpointSupportsIt()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        await tester.TestAsync("https://api.example.com/v1", "sk-x", "m-1",
            usesResponsesLite: false, protocol: ToolProtocol.Classic, supportsWebSearch: true);

        var post = recorder.Calls.Single(c => c.Body.Contains("\"tools\""));
        Assert.Contains("web_search", post.Body);
    }

    /// <summary>
    /// 小米的真实组合：classic + 无 custom + 无搜索 —— 这正是能通过的那套形态。
    /// </summary>
    [Fact]
    public async Task XiaomiShape_SendsOnlyFunctions()
    {
        var recorder = new Recorder((_, _) => Json("""{"id":"resp_1","status":"completed"}"""));
        var tester = Tester(recorder);

        var report = await tester.TestAsync("https://api.example.com/v1", "sk-x", "mimo-v2.6-pro",
            usesResponsesLite: false, protocol: ToolProtocol.Classic,
            supportsCustomTools: false, supportsWebSearch: false);

        Assert.True(report.Success);
        var post = recorder.Calls.Single(c => c.Body.Contains("\"tools\""));
        var tools = System.Text.Json.Nodes.JsonNode.Parse(post.Body)!.AsObject()["tools"]!.AsArray();

        Assert.All(tools, t => Assert.Equal("function", t!["type"]!.GetValue<string>()));
        Assert.DoesNotContain("additional_tools", post.Body);
        Assert.Null(recorder.Calls.Single(c => c.Body.Contains("\"tools\"")).LiteHeader);
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
            // 拒绝"带 tools 的请求"，模拟真实端点的 unsupported_feature。
            return body.Contains("\"tools\"")
                ? Json("""{"error":{"type":"unsupported_feature","message":"tool type 'web_search' is not supported by this gateway phase."}}""",
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
