using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GptPlusManager.Core.Codex;

/// <summary>自检中的一步。</summary>
public sealed record ConnectionTestStep(string Name, bool Passed, string Detail);

/// <summary>自检报告。</summary>
public sealed record ConnectionTestReport(
    bool Success,
    IReadOnlyList<ConnectionTestStep> Steps,
    string? Diagnosis);

/// <summary>
/// 在切换到第三方模式之前，先把这条链路走一遍。
///
/// <para><b>为什么需要它：</b>"兼容 Responses API" 只是个很粗的承诺，各家实现差异不小，
/// 而且差异往往只在真正带工具发请求时才暴露。Codex 那边的报错又大多没有指向性
/// （一句 "Unsupported model" 或 "unsupported_feature" 看不出该改什么），用户只能靠猜。
/// 这里分三步定位问题出在哪一层，并把端点原始错误原样带出来。</para>
///
/// <para>三步刻意递进：先确认能连上且密钥有效，再确认模型名被接受，最后确认
/// 带工具的请求（Codex 实际就是这么发的）也被接受——多数协议类问题只在第三步暴露。</para>
/// </summary>
public sealed class ProviderConnectionTester
{
    private readonly HttpClient _http;

    public ProviderConnectionTester(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

    public async Task<ConnectionTestReport> TestAsync(
        string baseUrl,
        string? apiKey,
        string model,
        bool usesResponsesLite,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<ConnectionTestStep>();
        var origin = (baseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin))
        {
            return new ConnectionTestReport(false, steps, "请先填写 Base URL。");
        }

        // 第一步：连得上吗？密钥对不对？
        var reachable = await GetAsync($"{origin}/models", apiKey, usesResponsesLite, cancellationToken)
            .ConfigureAwait(false);
        if (reachable.Status == 0)
        {
            steps.Add(new ConnectionTestStep("连通性", false, reachable.Message));
            return new ConnectionTestReport(false, steps, 
                "连不上端点。检查 Base URL 是否正确、网络是否可达。");
        }

        if (reachable.Status is 401 or 403)
        {
            steps.Add(new ConnectionTestStep("连通性", true, $"端点可达（HTTP {reachable.Status}）"));
            steps.Add(new ConnectionTestStep("API Key", false, reachable.Message));
            return new ConnectionTestReport(false, steps, "密钥被拒绝，请检查 API Key 是否正确、是否已过期。");
        }

        steps.Add(new ConnectionTestStep("连通性", true, $"端点可达（HTTP {reachable.Status}）"));

        // 第二步：模型名被接受吗？（大小写、是否存在）
        var minimal = await PostAsync(origin, apiKey, usesResponsesLite, BuildMinimalRequest(model), cancellationToken)
            .ConfigureAwait(false);
        if (!minimal.Ok)
        {
            steps.Add(new ConnectionTestStep("模型", false, minimal.Message));
            return new ConnectionTestReport(false, steps, Diagnose(minimal, model, withTools: false));
        }

        steps.Add(new ConnectionTestStep("模型", true, $"端点接受了模型 \"{model}\""));

        // 第三步：按 Codex 真实发出的形态带工具请求。
        //
        // Codex 的请求里没有顶层 tools 字段——工具是作为 input 里的 additional_tools 项传的；
        // 而且目录里的 use_responses_lite 会被翻成同名请求头一起发出。少了任一项，
        // 本来能用的配置也会被端点拒绝（实测正是如此），所以自检必须照抄这个形态，
        // 不能自己发明一个"看起来合理"的请求。
        var withTools = await PostAsync(origin, apiKey, usesResponsesLite, BuildToolRequest(model), cancellationToken)
            .ConfigureAwait(false);
        if (!withTools.Ok)
        {
            steps.Add(new ConnectionTestStep("工具调用", false, withTools.Message));
            return new ConnectionTestReport(false, steps, Diagnose(withTools, model, withTools: true));
        }

        steps.Add(new ConnectionTestStep("工具调用", true, "端点接受了带工具的请求"));

        return new ConnectionTestReport(true, steps,
            "三步全部通过，Codex 应该可以正常使用该模型。");
    }

    // ---------- 请求构造 ----------

    /// <summary>最小请求：只验模型名是否被接受。</summary>
    private static string BuildMinimalRequest(string model) => new JsonObject
    {
        ["model"] = model,
        ["input"] = "ping",
        ["stream"] = false,
    }.ToJsonString();

    /// <summary>
    /// 照抄 Codex 真实发出的请求形态。
    ///
    /// <para>只发最小请求不够：多数协议差异只在带工具时才暴露。而工具<b>不能</b>放在顶层
    /// <c>tools</c>——实测那样发会被端点以 <c>unsupported_feature</c> 拒绝，造成
    /// "本来是好的配置却报故障"的误判。Codex 是把工具作为 <c>additional_tools</c>
    /// 项放进 <c>input</c> 里的。</para>
    /// </summary>
    private static string BuildToolRequest(string model) => new JsonObject
    {
        ["model"] = model,
        ["input"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "additional_tools",
                ["role"] = "developer",
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "namespace",
                        ["name"] = "functions",
                        ["description"] = string.Empty,
                        ["tools"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "custom",
                                ["name"] = "exec",
                                ["description"] = "self-test tool",
                                ["format"] = new JsonObject { ["type"] = "text" },
                            },
                        },
                    },
                },
            },
            new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = "hi" },
                },
            },
        },
        ["stream"] = false,
        ["store"] = false,
        ["tool_choice"] = "auto",
        ["parallel_tool_calls"] = false,
    }.ToJsonString();

    // ---------- 诊断 ----------

    /// <summary>
    /// 把端点返回的原始错误翻译成"接下来该做什么"。
    /// 无法归类时原样展示——端点自己的话往往比我能编的解释更有用。
    /// </summary>
    private static string Diagnose(Response response, string model, bool withTools)
    {
        var body = response.Message;
        var lower = body.ToLowerInvariant();

        if (response.Status is 401 or 403 || lower.Contains("invalid api key") || lower.Contains("unauthorized"))
        {
            return "密钥无效或没有权限。请检查 API Key，以及该 Key 是否开通了这个模型。";
        }

        if (lower.Contains("unsupported model") || lower.Contains("not supported model") ||
            lower.Contains("model not found") || lower.Contains("does not exist") ||
            lower.Contains("unknown model"))
        {
            return $"端点不认识模型 \"{model}\"。模型 ID 由端点自定义、且常常区分大小写。" +
                   "请点「从端点获取可用模型」用端点自己声明的写法。";
        }

        if (lower.Contains("lite") || lower.Contains("unsupported_feature") ||
            lower.Contains("not supported for") || lower.Contains("does not support"))
        {
            return withTools
                ? "端点接受这个模型，但不接受 Codex 发出的工具/协议形态。\n\n" +
                  "这通常意味着该端点对 Responses API 的实现不完整，或需要额外的协议开关。" +
                  "把上面的原始错误发给端点提供方询问，是最快的路径——这不是本地配置能绕过的。"
                : "端点拒绝了这个请求的形态。把上面的原始错误发给端点提供方确认。";
        }

        if (response.Status == 404)
        {
            return "端点返回 404。通常是 Base URL 少了或多了一段路径（很多端点需要以 /v1 结尾）。";
        }

        if (response.Status is >= 500)
        {
            return "端点自己出错了（5xx）。稍后重试；持续如此请联系端点提供方。";
        }

        return "端点拒绝了请求。上面的原始错误是它给出的原因——把它发给端点提供方确认是最快的路径。";
    }

    // ---------- HTTP ----------

    private sealed record Response(int Status, bool Ok, string Message);

    private async Task<Response> GetAsync(
        string url, string? apiKey, bool usesResponsesLite, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, apiKey, usesResponsesLite);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new Response((int)response.StatusCode, response.IsSuccessStatusCode, Flatten(body));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new Response(0, false, exception.Message);
        }
    }

    private async Task<Response> PostAsync(
        string origin, string? apiKey, bool usesResponsesLite, string payload, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{origin}/responses");
            ApplyHeaders(request, apiKey, usesResponsesLite);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new Response((int)response.StatusCode, response.IsSuccessStatusCode, Flatten(body));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new Response(0, false, exception.Message);
        }
    }

    /// <summary>
    /// Codex 会把目录里的 <c>use_responses_lite</c> 翻译成这个请求头发出去，
    /// 端点据此切换协议形态。自检必须带上同一个头，否则会得到与真实使用不一致的结果
    /// （实测少了它会被端点整体拒绝）。
    /// </summary>
    private const string ResponsesLiteHeader = "x-openai-internal-codex-responses-lite";

    private static void ApplyHeaders(HttpRequestMessage request, string? apiKey, bool usesResponsesLite)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (usesResponsesLite)
        {
            request.Headers.TryAddWithoutValidation(ResponsesLiteHeader, "true");
        }
    }

    /// <summary>把错误体压成一行可读文本；是 JSON 就抽出 message 字段。</summary>
    private static string Flatten(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(无响应内容)";

        var text = body.Trim();
        if (text.Length < 2000 && (text.StartsWith('{') || text.StartsWith('[')))
        {
            try
            {
                var node = JsonNode.Parse(text);
                var message = node?["error"]?["message"]?.GetValue<string>()
                    ?? node?["error"]?.GetValue<string>()
                    ?? node?["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // 不是 JSON 就原样返回。
            }
        }

        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length > 400 ? flat[..400] + "…" : flat;
    }
}
