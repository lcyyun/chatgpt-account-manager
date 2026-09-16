namespace GptPlusManager.Core.Models;

public sealed record UsageWindow(
    double UsedPercent,
    string Description,
    double ResetAtUnix,
    double LimitWindowSeconds);

public sealed record UsageSnapshot(
    string PlanType,
    UsageWindow? Primary,
    UsageWindow? Secondary,
    int ResetCreditsAvailable,
    string Summary,
    DateTimeOffset CheckedAt,
    DateTimeOffset? SubscriptionUntil)
{
    public void ApplyTo(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        account.UsagePlan = PlanType;
        account.UsageP1 = Primary?.UsedPercent ?? -1;
        account.UsageP1Desc = Primary?.Description ?? string.Empty;
        account.UsageP1ResetAt = Primary?.ResetAtUnix ?? 0;
        account.UsageP2 = Secondary?.UsedPercent ?? -1;
        account.UsageP2Desc = Secondary?.Description ?? string.Empty;
        account.UsageP2ResetAt = Secondary?.ResetAtUnix ?? 0;
        account.ResetCreditsAvailable = ResetCreditsAvailable;
        account.UsageInfo = Summary;
        account.UsageCheckedAt = CheckedAt.LocalDateTime.ToString("MM-dd HH:mm");
        account.SubscriptionUntil = SubscriptionUntil?.LocalDateTime.ToString("yyyy-MM-dd") ?? string.Empty;
        account.SubscriptionUntilUnix = SubscriptionUntil?.ToUnixTimeSeconds() ?? 0;
    }
}

public sealed record ResetCreditResult(string Code, int WindowsReset, int AvailableCredits);

public sealed record OAuthLoginResult(Uri AuthorizationUri, TokenSet Tokens);
