namespace GptPlusManager.Core.Models;

public sealed class TokenSet
{
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private string _idToken = string.Empty;
    private string _email = string.Empty;
    private string _accountId = string.Empty;

    public string AccessToken { get => _accessToken; set => _accessToken = value ?? string.Empty; }
    public string RefreshToken { get => _refreshToken; set => _refreshToken = value ?? string.Empty; }
    public string IdToken { get => _idToken; set => _idToken = value ?? string.Empty; }
    public string Email { get => _email; set => _email = value ?? string.Empty; }

    /// <summary>
    /// ChatGPT 账号 ID。Codex 的 get_account_id() 直接读 auth.json 的
    /// tokens.account_id 字段，写成 null 会让它无法解析账号。
    /// </summary>
    public string AccountId { get => _accountId; set => _accountId = value ?? string.Empty; }

    public void NormalizeStrings()
    {
        AccessToken = AccessToken;
        RefreshToken = RefreshToken;
        IdToken = IdToken;
        Email = Email;
        AccountId = AccountId;
    }
}
