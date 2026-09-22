using GptPlusManager.Core.Codex;

namespace GptPlusManager.Wpf.Services;

/// <summary>第三方模型供应商的界面侧服务：装配 Core 的四个组件并暴露给窗口。</summary>
public interface IProviderManagerService : IDisposable
{
    /// <summary>目录文件所在目录，界面上展示给用户。</summary>
    string CatalogDirectory { get; }

    /// <summary>config.toml 的完整路径，界面上展示给用户。</summary>
    string ConfigPath { get; }

    Task<CodexConfigSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderDefinition>> LoadProvidersAsync(CancellationToken cancellationToken = default);

    Task<string?> GetActiveProviderIdAsync(CancellationToken cancellationToken = default);

    Task SaveProvidersAsync(
        IReadOnlyList<ProviderDefinition> providers,
        string? activeProviderId,
        CancellationToken cancellationToken = default);

    Task<string?> GetApiKeyAsync(string providerId, CancellationToken cancellationToken = default);

    Task SetApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken = default);

    Task<bool> HasApiKeyAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>生成目录并切入第三方模式。返回备份文件路径。</summary>
    Task<ProviderApplyResult> ApplyThirdPartyAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向端点查询它实际支持的模型 ID。可传未保存的 baseUrl / apiKey，
    /// 这样用户填完就能先验证，不必先保存。
    /// </summary>
    Task<ProviderModelList> FetchModelsAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 端到端自检：连通性 → 模型名 → 带工具的请求。
    /// 切换之前先跑一遍，把问题挡在重启 Codex 之前。
    /// </summary>
    /// <param name="usesResponsesLite">
    /// 该模型的目录条目是否启用 responses lite。Codex 会把它翻成请求头发给端点，
    /// 自检必须一致，否则结果与真实使用不符。
    /// </param>
    Task<ConnectionTestReport> TestConnectionAsync(
        string baseUrl, string? apiKey, string model, bool usesResponsesLite,
        CancellationToken cancellationToken = default);

    /// <summary>回到官方模式。返回备份文件路径（无改动时为 null）。</summary>
    Task<string?> ApplyOfficialAsync(CancellationToken cancellationToken = default);

    /// <summary>预览将要写入 config.toml 的内容，不落盘。</summary>
    Task<string> PreviewAsync(string? providerId, CancellationToken cancellationToken = default);

    /// <summary>最近一次自动备份的路径，供一键还原。</summary>
    string? FindLatestBackup();

    /// <summary>
    /// 若配置里的取 token 命令已不指向当前这份程序（程序被移动或改名过），
    /// 就地改写为当前路径。返回备份路径；无需修复时返回 null。
    /// </summary>
    Task<string?> RepairTokenCommandPathAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);

    Task RestartCodexAsync(CancellationToken cancellationToken = default);
}

/// <summary>切换结果，界面据此给出准确反馈。</summary>
public sealed record ProviderApplyResult(
    string BackupPath,
    int ModelCount,
    long CatalogBytes,
    CodexRoutingMode Mode,
    string? Note);

/// <summary>向端点查询可用模型的界面侧包装。</summary>
public sealed record ProviderModelList(
    bool Success,
    IReadOnlyList<string> Models,
    string? Error);
