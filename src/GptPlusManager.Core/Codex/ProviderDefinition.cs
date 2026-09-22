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
    /// 上下文窗口。第三方端点极少有官方那种 272k，留空则用 <see cref="ModelCatalogBuilder.DefaultContextWindow"/>。
    /// </summary>
    public int? ContextWindow { get; set; }

    /// <summary>该供应商的模型是否支持图片输入。</summary>
    public bool SupportsImages { get; set; }

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        BaseUrl = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');

        Models ??= [];
        foreach (var model in Models) model.Normalize();
        Models = [.. Models.Where(m => !string.IsNullOrWhiteSpace(m.Slug))];

        if (ContextWindow is <= 0) ContextWindow = null;
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

    public void Normalize()
    {
        Slug = (Slug ?? string.Empty).Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        // 显示名缺失时回落到 slug，避免目录里出现空名字。
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = Slug;
    }
}

public enum ProviderAuthMode
{
    /// <summary>命令式：由本应用输出 token，密钥 DPAPI 加密存储，配置文件里无明文。</summary>
    Command,

    /// <summary>明文写入 config.toml 的 experimental_bearer_token，可保留官方账号上下文。</summary>
    PlainToken,
}
