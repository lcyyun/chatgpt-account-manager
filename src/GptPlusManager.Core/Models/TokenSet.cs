namespace GptPlusManager.Core.Models;

public sealed class TokenSet
{
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private string _idToken = string.Empty;
    private string _email = string.Empty;

    public string AccessToken { get => _accessToken; set => _accessToken = value ?? string.Empty; }
    public string RefreshToken { get => _refreshToken; set => _refreshToken = value ?? string.Empty; }
    public string IdToken { get => _idToken; set => _idToken = value ?? string.Empty; }
    public string Email { get => _email; set => _email = value ?? string.Empty; }

    public void NormalizeStrings()
    {
        AccessToken = AccessToken;
        RefreshToken = RefreshToken;
        IdToken = IdToken;
        Email = Email;
    }
}
