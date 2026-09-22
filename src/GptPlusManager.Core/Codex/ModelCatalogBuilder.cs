using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GptPlusManager.Core.Codex;

/// <summary>目录生成结果。</summary>
public sealed record CatalogBuildResult(string OutputPath, IReadOnlyList<string> Slugs, long Bytes);

/// <summary>
/// 把第三方模型写成 Codex 能吃的 <c>model_catalog_json</c>。
///
/// <para><b>为什么必须克隆官方条目：</b>实测目录 schema 的字段几乎全是必填——
/// 手写一个最小条目会在 <c>support_verbosity</c>、<c>model_messages</c>、
/// <c>experimental_supported_tools</c> 等字段上直接报 parse error，导致 Codex 无法启动。
/// 其中 <c>model_messages</c> 还要求非空（空对象同样报错），而它装的是官方系统提示词。
/// 所以正确做法是拿<b>用户自己机器上</b>那份官方目录当模板克隆，只覆盖身份字段。</para>
///
/// <para>模板取自 <c>~/.codex/models_cache.json</c>：它是 Codex 自己抓下来的，
/// 永远与已安装版本同代，也就不会因为我们硬编码 schema 而随版本漂移。</para>
/// </summary>
public sealed class ModelCatalogBuilder
{
    /// <summary>
    /// 官方条目里只对官方服务有意义的字段。实测这些字段全部可以省略，
    /// 去掉是为了不让第三方条目带着官方升级话术、加速档与 hash 到处跑。
    /// </summary>
    private static readonly string[] OfficialOnlyFields =
    [
        "availability_nux",              // 官方升级/宣传语
        "available_access_programs",     // 官方访问计划
        "service_tiers",                 // 官方 "Fast" 加速档
        "additional_speed_tiers",
        "upgrade",
        "comp_hash",                     // 官方服务端编译 hash
        "multi_agent_version",           // 官方多智能体协议版本
        "multi_agent_reasoning_effort",
        "use_responses_lite",
        "effective_context_window_percent",
        "supports_experimental_context",
    ];

    public ModelCatalogBuilder(string? userProfile = null)
    {
        var profile = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile;
        TemplateCachePath = Path.Combine(profile, ".codex", "models_cache.json");
    }

    public string TemplateCachePath { get; }

    /// <summary>
    /// 为供应商生成目录文件。返回写出的模型 slug 列表。
    /// 任一模型条目缺失都会让 Codex 启动失败，因此只要有一个模型无法克隆就整体失败。
    /// </summary>
    public async Task<CatalogBuildResult> BuildAsync(
        ProviderDefinition provider,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.Normalize();
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("输出路径不能为空。", nameof(outputPath));

        var template = await LoadTemplateAsync(cancellationToken).ConfigureAwait(false);
        var models = new JsonArray();

        for (var i = 0; i < provider.Models.Count; i++)
        {
            var model = provider.Models[i];
            var entry = CloneForModel(template, provider, model, i + 1);
            models.Add(entry);
        }

        var document = new JsonObject { ["models"] = models };
        var json = document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // 目录里含中文提示词与表情，直接输出可读 UTF-8 而不是 \uXXXX 转义。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(outputPath, json, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        return new CatalogBuildResult(
            outputPath,
            [.. provider.Models.Select(m => m.Slug)],
            new FileInfo(outputPath).Length);
    }

    /// <summary>生成目录时使用的默认上下文窗口——第三方端点极少有 272k，用官方值会直接把请求撑爆。</summary>
    public const int DefaultContextWindow = 128_000;

    private static JsonObject CloneForModel(
        JsonObject template, ProviderDefinition provider, ProviderModel model, int priority)
    {
        var entry = template.DeepClone().AsObject();

        foreach (var field in OfficialOnlyFields) entry.Remove(field);

        entry["slug"] = model.Slug;
        entry["display_name"] = model.DisplayName;
        entry["description"] = string.IsNullOrWhiteSpace(provider.DisplayName)
            ? $"第三方模型，由 {provider.Id} 提供。"
            : $"第三方模型，由 {provider.DisplayName} 提供。";
        entry["priority"] = priority;
        entry["visibility"] = "list";
        entry["supported_in_api"] = true;

        // 上下文窗口由用户按供应商实际能力设定；缺省取保守值而非继承官方的 272k。
        var window = provider.ContextWindow > 0 ? provider.ContextWindow.Value : DefaultContextWindow;
        entry["context_window"] = window;
        entry["max_context_window"] = window;

        // 第三方端点不保证支持托管工具，关掉比让请求 400 好。
        entry.Remove("supports_search_tool");
        entry["supports_search_tool"] = false;

        if (provider.SupportsImages)
        {
            entry["input_modalities"] = new JsonArray("text", "image");
            entry["supports_image_detail_original"] = true;
        }
        else
        {
            entry["input_modalities"] = new JsonArray("text");
            entry["supports_image_detail_original"] = false;
        }

        return entry;
    }

    private async Task<JsonObject> LoadTemplateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(TemplateCachePath))
        {
            throw new FileNotFoundException(
                "找不到官方模型目录，无法生成第三方目录。请先启动一次 Codex（它会自动抓取该文件），然后重试。",
                TemplateCachePath);
        }

        var text = await File.ReadAllTextAsync(TemplateCachePath, cancellationToken).ConfigureAwait(false);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(text)?.AsObject()
                ?? throw new JsonException("目录内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"官方模型目录 {TemplateCachePath} 解析失败：{exception.Message}", exception);
        }

        var models = root["models"]?.AsArray();
        if (models is null || models.Count == 0)
        {
            throw new InvalidOperationException($"官方模型目录 {TemplateCachePath} 里没有任何模型条目。");
        }

        // 优先挑一个可见、且带完整 model_messages 的条目当模板——
        // model_messages 为空会让生成的目录解析失败。
        var candidates = models
            .Select(node => node?.AsObject())
            .Where(node => node is not null)
            .Cast<JsonObject>()
            .OrderByDescending(node => HasUsableMessages(node))
            .ThenByDescending(node => node["visibility"]?.GetValue<string>() == "list")
            .ThenBy(node => node["priority"]?.GetValue<int>() ?? int.MaxValue)
            .ToList();

        var template = candidates.FirstOrDefault(HasUsableMessages)
            ?? candidates.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"官方模型目录 {TemplateCachePath} 里没有可用作模板的模型条目。");

        if (!HasUsableMessages(template))
        {
            throw new InvalidOperationException(
                $"官方模型目录 {TemplateCachePath} 中所有条目的 model_messages 都是空的，无法作为模板。" +
                "请删除该文件并重新启动一次 Codex 让它重新抓取。");
        }

        return template;
    }

    private static bool HasUsableMessages(JsonObject node) =>
        node["model_messages"] is JsonObject messages && messages.Count > 0;
}
