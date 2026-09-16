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
    Task BackupJsonAsync(CancellationToken cancellationToken = default);
    Task ExportTextAsync(CancellationToken cancellationToken = default);
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
