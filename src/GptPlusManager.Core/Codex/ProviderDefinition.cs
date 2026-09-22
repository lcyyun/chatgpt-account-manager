namespace GptPlusManager.Core.Codex;

/// <summary>第三方模型供应商的定义（不含密钥）。</summary>
public sealed class ProviderDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>该供应商下的模型。</summary>
    public List<ProviderModel> Models { get; set; } = [];

    /// <summary>密钥注入方式。</summary>
    public ProviderAuthMode AuthMode { get; set; } = ProviderAuthMode.Command;

    /// <summary>
    /// 旧版遗留的供应商级上下文窗口。<b>只用于迁移</b>：<see cref="Normalize"/> 会把它
    /// 下发给尚未单独设置的模型，然后清空。上下文窗口现在是逐模型设置。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("ContextWindow")]
    public int? LegacyContextWindow { get; set; }

    /// <summary>
    /// 供应商级默认：模型是否支持图片输入（模型自己可覆盖）。
    /// 同样属于旧版遗留——现在界面上是逐模型勾选。
    /// </summary>
    public bool SupportsImages { get; set; }

    /// <summary>参与生成的模型（已启用的）。</summary>
    public IReadOnlyList<ProviderModel> EnabledModels =>
        [.. Models.Where(m => m.Enabled)];

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        BaseUrl = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');

        Models ??= [];
        foreach (var model in Models) model.Normalize();

        // 迁移旧数据：早期版本把上下文窗口与图片能力放在供应商级，逐模型字段为空。
        // 不迁移的话，界面上看不到的那个旧值会失效，用户配置的窗口就"凭空丢了"。
        // 只在模型自己没设时才继承，不覆盖用户之后逐模型的修改。
        if (LegacyContextWindow is > 0)
        {
            foreach (var model in Models)
            {
                model.ContextWindow ??= LegacyContextWindow;
            }
            LegacyContextWindow = null;
        }

        foreach (var model in Models)
        {
            if (!model.SupportsImages) model.SupportsImages = SupportsImages;
        }

        Models = [.. Models.Where(m => !string.IsNullOrWhiteSpace(m.Slug))];
    }

    /// <summary>用于写入 TOML 的供应商表名：只允许安全字符，其余一律替换掉。</summary>
    public string SafeTomlKey()
    {
        var key = new string([.. Id.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')]);
        return key.Length == 0 ? "provider" : key;
    }
}

public sealed class ProviderModel
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 该模型的上下文窗口。逐模型设置——同一家的不同模型窗口常常差很多。
    /// 留空则用 <see cref="ModelCatalogBuilder.DefaultContextWindow"/>。
    /// </summary>
    public int? ContextWindow { get; set; }

    /// <summary>该模型是否支持图片输入。</summary>
    public bool SupportsImages { get; set; }

    /// <summary>
    /// 是否启用。关掉的模型仍留在列表里（方便以后开回来），
    /// 但不会写进 Codex 的模型目录——因此不会出现在选择器中。
    /// </summary>
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
        Slug = (Slug ?? string.Empty).Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        // 显示名缺失时回落到 slug，避免目录里出现空名字。
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Slug;
        if (ContextWindow is <= 0) ContextWindow = null;
    }
}

public enum ProviderAuthMode
{
    /// <summary>命令式：由本应用输出 token，密钥 DPAPI 加密存储，配置文件里无明文。</summary>
    Command,

    /// <summary>明文写入 config.toml 的 experimental_bearer_token，可保留官方账号上下文。</summary>
    PlainToken,
}
