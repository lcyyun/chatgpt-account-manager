using System.Text.Json.Serialization;

namespace GptPlusManager.Core.Models;

public sealed class AccountRecord
{
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _secret = string.Empty;
    private string _note = string.Empty;
    private string _purchasedAt = string.Empty;
    private string _usageInfo = string.Empty;
    private string _usageCheckedAt = string.Empty;
    private string _usageP1Desc = string.Empty;
    private string _usageP2Desc = string.Empty;
    private string _usagePlan = string.Empty;
    private string _subscriptionUntil = string.Empty;
    private string _codexSwitchedAt = string.Empty;
    private string _createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    public string Email { get => _email; set => _email = value ?? string.Empty; }
    public string Password { get => _password; set => _password = value ?? string.Empty; }
    public string Secret { get => _secret; set => _secret = value ?? string.Empty; }
    public string Note { get => _note; set => _note = value ?? string.Empty; }
    public string PurchasedAt { get => _purchasedAt; set => _purchasedAt = value ?? string.Empty; }
    public string UsageInfo { get => _usageInfo; set => _usageInfo = value ?? string.Empty; }
    public string UsageCheckedAt { get => _usageCheckedAt; set => _usageCheckedAt = value ?? string.Empty; }
    public double UsageP1 { get; set; } = -1;
    public string UsageP1Desc { get => _usageP1Desc; set => _usageP1Desc = value ?? string.Empty; }
    public double UsageP1ResetAt { get; set; }
    public double UsageP2 { get; set; } = -1;
    public string UsageP2Desc { get => _usageP2Desc; set => _usageP2Desc = value ?? string.Empty; }
    public double UsageP2ResetAt { get; set; }
    public string UsagePlan { get => _usagePlan; set => _usagePlan = value ?? string.Empty; }
    public string SubscriptionUntil { get => _subscriptionUntil; set => _subscriptionUntil = value ?? string.Empty; }
    public double SubscriptionUntilUnix { get; set; }
    public int ResetCreditsAvailable { get; set; }
    public bool IsInvalid { get; set; }
    public string CodexSwitchedAt { get => _codexSwitchedAt; set => _codexSwitchedAt = value ?? string.Empty; }
    public string CreatedAt { get => _createdAt; set => _createdAt = value ?? string.Empty; }

    [JsonIgnore]
    public string DisplayLine => string.IsNullOrEmpty(Secret)
        ? $"{Email} | {Password}"
        : $"{Email} | {Password} | {Secret}";

    public void NormalizeStrings()
    {
        Email = Email;
        Password = Password;
        Secret = Secret;
        Note = Note;
        PurchasedAt = PurchasedAt;
        UsageInfo = UsageInfo;
        UsageCheckedAt = UsageCheckedAt;
        UsageP1Desc = UsageP1Desc;
        UsageP2Desc = UsageP2Desc;
        UsagePlan = UsagePlan;
        SubscriptionUntil = SubscriptionUntil;
        CodexSwitchedAt = CodexSwitchedAt;
        CreatedAt = CreatedAt;
    }
}
