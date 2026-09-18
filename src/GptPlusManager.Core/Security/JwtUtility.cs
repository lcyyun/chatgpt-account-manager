using System.Text;
using System.Text.Json;

namespace GptPlusManager.Core.Security;

public static class JwtUtility
{
    public static IReadOnlyDictionary<string, JsonElement> GetClaims(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return new Dictionary<string, JsonElement>();
        }

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return new Dictionary<string, JsonElement>();
            }

            var value = parts[1].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            return document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    public static string? GetStringClaim(string? jwt, string claimName)
    {
        var claims = GetClaims(jwt);
        if (!claims.TryGetValue(claimName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    /// <summary>
    /// 读取嵌套命名空间下的 claim，例如
    /// <c>https://api.openai.com/auth</c> → <c>chatgpt_account_id</c>。
    /// </summary>
    public static string? GetNestedStringClaim(string? jwt, string parentClaim, string childClaim)
    {
        var claims = GetClaims(jwt);
        if (!claims.TryGetValue(parentClaim, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parent.TryGetProperty(childClaim, out var value))
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// ChatGPT 账号 ID。它不在 id_token 顶层，而是位于
    /// <c>https://api.openai.com/auth</c> 命名空间内。
    ///
    /// 这一点很关键：Codex 的 <c>get_account_id()</c> 直接读取 auth.json 的
    /// <c>tokens.account_id</c> 字段，写错或写成 null 会导致其无法解析账号
    /// （表现为 "waiting for a ChatGPT account id"，账号信息取不到）。
    /// </summary>
    public static string? GetChatGptAccountId(string? jwt) =>
        GetNestedStringClaim(jwt, "https://api.openai.com/auth", "chatgpt_account_id");

    public static DateTimeOffset? GetExpiration(string? jwt)
    {
        var claims = GetClaims(jwt);
        if (!claims.TryGetValue("exp", out var value))
        {
            return null;
        }

        if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
            || (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out seconds)))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
    }
}
