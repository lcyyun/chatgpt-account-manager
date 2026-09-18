using System.Text;
using System.Text.Json;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Tests;

/// <summary>
/// JWT claim 解析测试。重点覆盖 ChatGPT 账号 ID：它不在 id_token 顶层，
/// 而是嵌套在 https://api.openai.com/auth 命名空间内。
///
/// 回归背景：Codex 的 get_account_id() 直接读取 auth.json 的 tokens.account_id。
/// 早期实现只查顶层 claim，导致每次切换账号都把该字段写成 null，Codex 随即
/// 报 "waiting for a ChatGPT account id" 并无法解析账号信息。
/// </summary>
public class JwtUtilityTests
{
    private const string AuthNamespace = "https://api.openai.com/auth";

    /// <summary>构造一个未签名但结构真实的 JWT（header.payload.signature）。</summary>
    private static string BuildJwt(object payload)
    {
        static string Encode(string text) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode("""{"alg":"RS256","typ":"JWT"}""");
        var body = Encode(JsonSerializer.Serialize(payload));
        return $"{header}.{body}.signature";
    }

    private static string BuildIdToken(string email, string accountId) =>
        BuildJwt(new Dictionary<string, object>
        {
            ["email"] = email,
            ["sub"] = "user-123",
            [AuthNamespace] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = accountId,
                ["chatgpt_plan_type"] = "plus",
                ["chatgpt_user_id"] = "user-123",
            },
        });

    [Fact]
    public void ReadsAccountIdFromNestedAuthNamespace()
    {
        var token = BuildIdToken("user@example.com", "11111111-2222-3333-4444-555555555555");

        Assert.Equal("11111111-2222-3333-4444-555555555555", JwtUtility.GetChatGptAccountId(token));
    }

    [Fact]
    public void TopLevelClaimLookupDoesNotFindNestedAccountId()
    {
        // 明确固化这个语义：顶层查不到，这正是当年写坏 auth.json 的原因。
        var token = BuildIdToken("user@example.com", "abc-123");

        Assert.Null(JwtUtility.GetStringClaim(token, "chatgpt_account_id"));
        Assert.Equal("abc-123", JwtUtility.GetChatGptAccountId(token));
    }

    [Fact]
    public void ReadsTopLevelEmailClaim()
    {
        var token = BuildIdToken("user@example.com", "abc-123");

        Assert.Equal("user@example.com", JwtUtility.GetStringClaim(token, "email"));
    }

    [Fact]
    public void FallsBackToProfileNamespaceForEmail()
    {
        // 部分令牌把 email 放在 https://api.openai.com/profile 里。
        var token = BuildJwt(new Dictionary<string, object>
        {
            ["https://api.openai.com/profile"] = new Dictionary<string, object>
            {
                ["email"] = "profile@example.com",
            },
            [AuthNamespace] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = "abc-123",
            },
        });

        Assert.Equal("profile@example.com",
            JwtUtility.GetNestedStringClaim(token, "https://api.openai.com/profile", "email"));
        Assert.Equal("abc-123", JwtUtility.GetChatGptAccountId(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public void ReturnsNullForMalformedTokens(string token)
    {
        Assert.Null(JwtUtility.GetChatGptAccountId(token));
        Assert.Empty(JwtUtility.GetClaims(token));
    }

    [Fact]
    public void ReturnsNullWhenAuthNamespaceMissing()
    {
        var token = BuildJwt(new Dictionary<string, object> { ["email"] = "user@example.com" });

        Assert.Null(JwtUtility.GetChatGptAccountId(token));
    }

    [Fact]
    public void ReadsExpirationFromNumericClaim()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = BuildJwt(new Dictionary<string, object> { ["exp"] = exp });

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp), JwtUtility.GetExpiration(token));
    }
}
