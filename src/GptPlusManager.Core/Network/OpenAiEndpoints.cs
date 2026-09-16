namespace GptPlusManager.Core.Network;

public static class OpenAiEndpoints
{
    public const string OAuthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string OAuthIssuer = "https://auth.openai.com";
    public const string OAuthAuthorizeUrl = OAuthIssuer + "/oauth/authorize";
    public const string OAuthTokenUrl = OAuthIssuer + "/oauth/token";
    public const string OAuthScope = "openid profile email offline_access";
    public const string UsageApiUrl = "https://chatgpt.com/backend-api/wham/usage";
    public const string AccountCheckUrl = "https://chatgpt.com/backend-api/wham/accounts/check";
    public const string ResetCreditConsumeUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume";
    public const string UserAgent = "GptPlusManager/2.0 (Codex-compatible; Windows)";
}
