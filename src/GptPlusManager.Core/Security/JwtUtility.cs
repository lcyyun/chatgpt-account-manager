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
