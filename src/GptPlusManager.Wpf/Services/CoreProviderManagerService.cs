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
    private readonly ProviderModelProbe _probe = new();
    private readonly ProviderConnectionTester _tester = new();
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

        // 预检：端点认不认这些模型 ID。
        //
        // 模型 ID 由端点自定义，大小写规则各家不同（实测有的端点区分大小写：
        // Foo-Bar 被拒，必须写 foo-bar）。填错时 Codex 要到真正发请求
        // 才报 "Unsupported model"，完全看不出该改成什么，所以在这里就问清楚。
        //
        // 探测失败（端点不支持 /models、网络不通等）不拦——不能因为一个探测接口
        // 不可用就挡住本来能用的配置。
        var note = string.Empty;
        var probe = await _probe.ListModelsAsync(provider.BaseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        if (probe.Success)
        {
            var corrections = new List<string>();
            var dropped = new List<string>();

            foreach (var model in provider.Models.ToList())
            {
                if (probe.Models.Contains(model.Slug, StringComparer.Ordinal)) continue;

                // 只有大小写不同的，按端点的写法修正——这必然是用户的本意。
                var exact = probe.Models.FirstOrDefault(m =>
                    string.Equals(m, model.Slug, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    corrections.Add($"{model.Slug} → {exact}");
                    model.Slug = exact;
                    continue;
                }

                // 端点明确不提供：留在列表里只会在 Codex 里点一次错一次，去掉它。
                // 但绝不静默——下面会把去掉哪些写进提示。
                dropped.Add(model.Slug);
                provider.Models.Remove(model);
            }

            if (provider.Models.Count == 0)
            {
                var available = string.Join("\n  ", probe.Models.Take(30));
                throw new InvalidOperationException(
                    "端点不支持你配置的任何模型，无法切换。\n\n" +
                    $"该端点实际可用：\n  {available}\n\n" +
                    "请点「从端点获取可用模型」一键填入，或手动改成上面的写法（注意大小写）。");
            }

            var notes = new List<string>();
            if (corrections.Count > 0) notes.Add("已按端点实际大小写修正：" + string.Join("、", corrections));
            if (dropped.Count > 0)
            {
                notes.Add($"已跳过端点不支持的模型：{string.Join("、", dropped)}" +
                          "（可在该端点的 /models 列表里确认正确写法后重新添加）");
            }
            if (notes.Count > 0)
            {
                note = string.Join("　", notes);
                // 修正与剔除都要落盘，否则下次应用又会拿到同样的错配置。
                await _registry.SaveAsync(providers, provider.Id, cancellationToken).ConfigureAwait(false);
            }
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

        return new ProviderApplyResult(
            backup, provider.Models.Count, build.Bytes, CodexRoutingMode.ThirdParty,
            note.Length > 0 ? note : null);
    }

    public async Task<ProviderModelList> FetchModelsAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken = default)
    {
        var result = await _probe.ListModelsAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        return new ProviderModelList(result.Success, result.Models, result.Error);
    }

    public async Task<ConnectionTestReport> TestConnectionAsync(
        string baseUrl, string? apiKey, string model, bool usesResponsesLite,
        CancellationToken cancellationToken = default)
    {
        // 自检要与真实请求一致：Codex 会把目录里的 use_responses_lite 翻成请求头。
        // 没显式传时，直接问目录生成器将来会写什么，避免这里再维护一个平行开关。
        var lite = usesResponsesLite || await _catalogs
            .TemplateUsesResponsesLiteAsync(cancellationToken)
            .ConfigureAwait(false);

        return await _tester.TestAsync(baseUrl, apiKey, model, lite, cancellationToken).ConfigureAwait(false);
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

    public Task<string?> RepairTokenCommandPathAsync(CancellationToken cancellationToken = default) =>
        _config.RepairTokenCommandPathAsync(cancellationToken);

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
