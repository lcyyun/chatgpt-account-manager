using System.Text;
using System.Text.Json.Nodes;
using GptPlusManager.Core.Codex;
using GptPlusManager.Core.Models;

namespace GptPlusManager.Core.Tests;

public sealed class CodexAuthStoreTests
{
    [Fact]
    public async Task SaveAsync_MergesTokensAndPreservesUnknownFieldsInInjectedProfile()
    {
        using var profile = new TemporaryDirectory();
        using var store = new CodexAuthStore(profile.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.AuthFilePath)!);
        const string existingJson = """
            {
              "auth_mode": "fixture-mode",
              "OPENAI_API_KEY": "fixture-api-key",
              "unknown_root": { "keep": true },
              "tokens": {
                "access_token": "old-access",
                "refresh_token": "old-refresh",
                "id_token": "old-id",
                "unknown_token": "keep-token-field"
              }
            }
            """;
        await File.WriteAllTextAsync(store.AuthFilePath, existingJson, new UTF8Encoding(false));
        var tokens = new TokenSet
        {
            AccessToken = "fixture-access-token",
            RefreshToken = "fixture-refresh-token",
            IdToken = CreateUnsignedJwt("fixture.user@example.test", "fixture-account-id")
        };

        var backupPath = await store.SaveAsync(tokens);

        Assert.NotNull(backupPath);
        Assert.True(File.Exists(backupPath));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(store.AuthFilePath))!.AsObject();
        Assert.Equal("fixture-mode", root["auth_mode"]!.GetValue<string>());
        Assert.Equal("fixture-api-key", root["OPENAI_API_KEY"]!.GetValue<string>());
        Assert.True(root["unknown_root"]!["keep"]!.GetValue<bool>());
        var savedTokens = root["tokens"]!.AsObject();
        Assert.Equal("fixture-access-token", savedTokens["access_token"]!.GetValue<string>());
        Assert.Equal("fixture-refresh-token", savedTokens["refresh_token"]!.GetValue<string>());
        Assert.Equal(tokens.IdToken, savedTokens["id_token"]!.GetValue<string>());
        Assert.Equal("fixture-account-id", savedTokens["account_id"]!.GetValue<string>());
        Assert.Equal("keep-token-field", savedTokens["unknown_token"]!.GetValue<string>());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(store.AuthFilePath)!, "*.tmp"));

        var loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("fixture.user@example.test", loaded.Email);
        Assert.Equal("fixture-access-token", loaded.AccessToken);
    }

    private static string CreateUnsignedJwt(string email, string accountId)
    {
        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64Url($"{{\"email\":\"{email}\",\"chatgpt_account_id\":\"{accountId}\"}}");
        return $"{header}.{payload}.fixture-signature";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
