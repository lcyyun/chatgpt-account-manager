using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GptPlusManager.Core.Codex;

/// <summary>探测结果。</summary>
public sealed record ModelProbeResult
{
    public bool Success { get; init; }

    /// <summary>端点声明的模型 ID 列表（原样保留大小写）。</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>失败原因，直接展示给用户。</summary>
    public string? Error { get; init; }

    public static ModelProbeResult Ok(IReadOnlyList<string> models) => new() { Success = true, Models = models };
    public static ModelProbeResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// 向第三方端点查询它实际支持的模型名（<c>GET {baseUrl}/models</c>）。
///
/// <para><b>为什么需要这个：</b>模型 ID 是端点自己定义的，大小写与命名规则各不相同，
/// 而且完全没有约定。实测小米端点就区分大小写——填 <c>MiMo-V2.6-Pro</c> 会被拒，
/// 必须写 <c>mimo-v2.6-pro</c>。用户手填几乎必然踩坑，而 Codex 报的
/// "Unsupported model" 完全没提示该怎么改。所以让端点自己报出清单。</para>
/// </summary>
public sealed class ProviderModelProbe
{
    private readonly HttpClient _http;

    public ProviderModelProbe(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

    public async Task<ModelProbeResult> ListModelsAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return ModelProbeResult.Fail("请先填写 Base URL。");
        }

        var url = $"{baseUrl.TrimEnd('/')}/models";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ModelProbeResult.Fail(
                    $"端点返回 {(int)response.StatusCode}：{Truncate(body)}");
            }

            var models = ParseModelIds(body);
            return models.Count == 0
                ? ModelProbeResult.Fail("端点没有返回任何模型（可能是它不支持 /models 列表）。请手动填写模型 ID。")
                : ModelProbeResult.Ok(models);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelProbeResult.Fail("请求超时，端点没有响应。");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return ModelProbeResult.Fail($"连接失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 解析模型列表。兼容 OpenAI 的 <c>{"data":[{"id":...}]}</c> 与更宽松的
    /// <c>{"models":[...]}</c> / <c>["id", ...]</c> 形式——中转站实现并不统一。
    /// </summary>
    private static List<string> ParseModelIds(string body)
    {
        var ids = new List<string>();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return ids;
        }

        var array = root switch
        {
            JsonArray direct => direct,
            JsonObject obj when obj["data"] is JsonArray data => data,
            JsonObject obj when obj["models"] is JsonArray models => models,
            JsonObject obj when obj["model_ids"] is JsonArray idsArray => idsArray,
            _ => null,
        };

        if (array is null) return ids;

        foreach (var node in array)
        {
            // 字符串数组，或带 id / name / model 字段的对象数组，都接受。
            var id = node switch
            {
                JsonValue value when value.TryGetValue<string>(out var s) => s,
                JsonObject item when item["id"] is JsonValue v && v.TryGetValue<string>(out var s) => s,
                JsonObject item when item["name"] is JsonValue v && v.TryGetValue<string>(out var s) => s,
                JsonObject item when item["model"] is JsonValue v && v.TryGetValue<string>(out var s) => s,
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string Truncate(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length > 200 ? flat[..200] + "…" : flat;
    }
}
