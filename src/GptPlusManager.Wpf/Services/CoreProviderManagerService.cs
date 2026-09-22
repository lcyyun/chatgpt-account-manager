using System.IO;
using GptPlusManager.Core.Codex;
using GptPlusManager.Core.Services;

namespace GptPlusManager.Wpf.Services;

public sealed class CoreProviderManagerService : IProviderManagerService
{
    private readonly CodexConfigStore _config;
    private readonly ProviderRegistry _registry;
    private readonly ProviderSecrets _secrets;
    private readonly ModelCatalogBuilder _catalogs;
    private readonly ClientProcessService _processes = new();

    /// <param name="userProfile">用户主目录（决定 <c>~/.codex</c> 位置）；测试可注入临时目录。</param>
    /// <param name="dataRoot">供应商定义的存放目录；默认与应用的其它数据同处 Documents\gptplus。</param>
    public CoreProviderManagerService(string? userProfile = null, string? dataRoot = null)
    {
        _config = new CodexConfigStore(userProfile);
        _registry = new ProviderRegistry(dataRoot);
        _secrets = new ProviderSecrets(dataRoot);
        _catalogs = new ModelCatalogBuilder(userProfile);

        // 目录文件放进 ~/.codex 而不是应用的数据目录：它和 config.toml 是一根绳上的，
        // 必须同生共死。若目录被移到别处而 config.toml 还指着它，Codex 会直接启动失败。
        CatalogDirectory = Path.Combine(_config.CodexHome, "gptplus-catalogs");
    }

    public string CatalogDirectory { get; }

    public string ConfigPath => _config.ConfigPath;

    public Task<CodexConfigSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _config.LoadAsync(cancellationToken);

    public Task<IReadOnlyList<ProviderDefinition>> LoadProvidersAsync(CancellationToken cancellationToken = default) =>
        _registry.LoadAsync(cancellationToken);

    public Task<string?> GetActiveProviderIdAsync(CancellationToken cancellationToken = default) =>
        _registry.GetActiveProviderIdAsync(cancellationToken);

    public Task SaveProvidersAsync(
        IReadOnlyList<ProviderDefinition> providers,
        string? activeProviderId,
        CancellationToken cancellationToken = default) =>
        _registry.SaveAsync(providers, activeProviderId, cancellationToken);

    public Task<string?> GetApiKeyAsync(string providerId, CancellationToken cancellationToken = default) =>
        _secrets.GetAsync(providerId, cancellationToken);

    public Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken = default) =>
        _secrets.SetAsync(providerId, apiKey, cancellationToken);

    public Task<bool> HasApiKeyAsync(string providerId, CancellationToken cancellationToken = default) =>
        _secrets.HasAsync(providerId, cancellationToken);

    public async Task<ProviderApplyResult> ApplyThirdPartyAsync(
        string providerId, CancellationToken cancellationToken = default)
    {
        var providers = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(p =>
            string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"找不到供应商 \"{providerId}\"。");

        if (provider.Models.Count == 0)
        {
            throw new InvalidOperationException("该供应商还没有模型，请先添加至少一个模型。");
        }

        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Base URL 必须是完整的 http:// 或 https:// 地址。");
        }

        // 密钥先取出来：命令式与明文式都需要它，缺了就没法发请求。
        var apiKey = await _secrets.GetAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("该供应商还没有设置 API Key。");
        }

        // 先建目录再改配置：目录写入失败时配置还没动，Codex 仍处于可用状态。
        var catalogPath = Path.Combine(CatalogDirectory, $"{provider.SafeTomlKey()}.json");
        var build = await _catalogs.BuildAsync(provider, catalogPath, cancellationToken).ConfigureAwait(false);

        // PlainToken 模式才把密钥写进配置文件；Command 模式配置里不留明文。
        var tokenForConfig = provider.AuthMode == ProviderAuthMode.PlainToken ? apiKey : null;
        var backup = await _config
            .ApplyThirdPartyAsync(provider, build.OutputPath, tokenForConfig, cancellationToken)
            .ConfigureAwait(false);

        if (backup is null)
        {
            throw new InvalidOperationException("配置写入被跳过（未产生备份），请检查 config.toml 是否可写。");
        }

        await _registry.SaveAsync(providers, provider.Id, cancellationToken).ConfigureAwait(false);

        return new ProviderApplyResult(backup, provider.Models.Count, build.Bytes, CodexRoutingMode.ThirdParty);
    }

    public async Task<string?> ApplyOfficialAsync(CancellationToken cancellationToken = default)
    {
        var backup = await _config.ApplyOfficialAsync(cancellationToken).ConfigureAwait(false);
        var providers = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _registry.SaveAsync(providers, null, cancellationToken).ConfigureAwait(false);
        return backup;
    }

    public async Task<string> PreviewAsync(string? providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return await _config.PreviewOfficialAsync(cancellationToken).ConfigureAwait(false);
        }

        var providers = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(p =>
            string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null || provider.Models.Count == 0)
        {
            return await _config.PreviewOfficialAsync(cancellationToken).ConfigureAwait(false);
        }

        var apiKey = await _secrets.GetAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        var tokenForConfig = provider.AuthMode == ProviderAuthMode.PlainToken ? apiKey : null;
        var catalogPath = Path.Combine(CatalogDirectory, $"{provider.SafeTomlKey()}.json");

        return await _config
            .PreviewThirdPartyAsync(provider, catalogPath, tokenForConfig, cancellationToken)
            .ConfigureAwait(false);
    }

    public string? FindLatestBackup()
    {
        var directory = Path.GetDirectoryName(_config.ConfigPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

        return Directory.GetFiles(directory, "config.toml.*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default) =>
        _config.RestoreAsync(backupPath, cancellationToken);

    public Task RestartCodexAsync(CancellationToken cancellationToken = default) =>
        _processes.RestartAsync(cancellationToken: cancellationToken);

    public void Dispose()
    {
        _config.Dispose();
        _registry.Dispose();
        _secrets.Dispose();
    }
}
