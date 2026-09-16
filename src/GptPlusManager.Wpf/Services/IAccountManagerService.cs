using GptPlusManager.Core.Services;

namespace GptPlusManager.Wpf.Services;

public interface IAccountAuthorizationSession : IDisposable
{
    Uri AuthorizationUri { get; }
    Task<AuthResult> Completion { get; }
    bool Reopen();
    void Cancel();
}

public interface IAccountManagerService : IDisposable
{
    Task<IReadOnlyList<AccountSnapshot>> LoadAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountSnapshot> AddAccountAsync(AccountEditRequest request, CancellationToken cancellationToken = default);
    Task<AccountSnapshot> UpdateAccountAsync(Guid accountId, AccountEditRequest request, CancellationToken cancellationToken = default);
    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SaveOrderAsync(IReadOnlyList<Guid> accountIds, CancellationToken cancellationToken = default);
    IAccountAuthorizationSession StartAuthorization(Guid accountId, CancellationToken cancellationToken = default);
    Task<UsageSnapshot> QueryUsageAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<ResetCreditUiResult> ConsumeResetCreditAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SwitchCodexAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<AccountSnapshot> SetInvalidAsync(Guid accountId, bool isInvalid, CancellationToken cancellationToken = default);

    /// <summary>导出文件名所在的目录，界面上直接展示给用户。</summary>
    string ExportsDirectory { get; }

    /// <summary>写出 JSON 备份，返回完整文件路径。</summary>
    Task<string> BackupJsonAsync(CancellationToken cancellationToken = default);

    /// <summary>写出纯文本清单，返回完整文件路径。</summary>
    Task<string> ExportTextAsync(CancellationToken cancellationToken = default);

    /// <summary>按文件名倒序列出导出目录中的可导入文件，最新的在最前。</summary>
    IReadOnlyList<string> ListExportFiles();

    /// <summary>把粘贴或读取到的内容合并进现有账号，导入前会自动备份当前数据。</summary>
    Task<ImportUiResult> ImportTextAsync(string content, string sourceLabel, CancellationToken cancellationToken = default);

    Task<bool> GetKeepAliveAsync(CancellationToken cancellationToken = default);
    Task SetKeepAliveAsync(bool enabled, CancellationToken cancellationToken = default);
    Task RestartCodexAsync(CancellationToken cancellationToken = default);
    string GenerateTotp(string secret, DateTimeOffset now);
}

public sealed record AccountEditRequest(
    string Email,
    string Password,
    string TwoFactorSecret,
    DateTimeOffset? PurchasedAt,
    string Note);

public sealed record AccountSnapshot(
    Guid Id,
    string Email,
    string Password,
    string TwoFactorSecret,
    DateTimeOffset? PurchasedAt,
    string Note,
    string PlanName,
    DateTimeOffset? SubscriptionUntil,
    bool IsAuthorized,
    bool IsInvalid,
    bool IsCurrentCodex,
    bool HasPrimaryUsage,
    bool HasSecondaryUsage,
    double PrimaryUsagePercent,
    double SecondaryUsagePercent,
    string PrimaryDescription,
    string SecondaryDescription,
    DateTimeOffset? PrimaryResetsAt,
    DateTimeOffset? SecondaryResetsAt,
    int ResetCreditsAvailable,
    string UsageCheckedAt);

public sealed record AuthResult(bool IsAuthorized, bool IsInvalid, string StatusText, bool IsCurrentCodex);

public sealed record UsageSnapshot(
    string PlanName,
    bool HasPrimaryUsage,
    bool HasSecondaryUsage,
    double PrimaryPercent,
    double SecondaryPercent,
    string PrimaryDescription,
    string SecondaryDescription,
    DateTimeOffset? PrimaryResetsAt,
    DateTimeOffset? SecondaryResetsAt,
    DateTimeOffset? SubscriptionUntil,
    int ResetCreditsAvailable,
    bool IsInvalid);

public sealed record ResetCreditUiResult(string Code, int WindowsReset, int AvailableCredits, string Message);

/// <summary>导入结果，含新增 / 更新 / 跳过数量与导入前的备份路径。</summary>
public sealed record ImportUiResult(string FilePath, ImportSummary Summary, string? BackupPath);
