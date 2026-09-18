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

    /// <summary>
    /// 回归测试：Codex 的 get_account_id() 直接读取 auth.json 的 tokens.account_id。
    /// 早期实现从 id_token 顶层取 chatgpt_account_id（实际嵌套在
    /// https://api.openai.com/auth 内），导致该字段被写成 null，Codex 随即报
    /// "waiting for a ChatGPT account id" 且无法解析账号信息。
    /// </summary>
    [Fact]
    public async Task SaveAsync_WritesRealAccountIdNot_Null()
    {
        using var profile = new TemporaryDirectory();
        using var store = new CodexAuthStore(profile.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.AuthFilePath)!);

        var tokens = new TokenSet
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            IdToken = CreateUnsignedJwt("user@example.test", "11111111-2222-3333-4444-555555555555")
        };

        await store.SaveAsync(tokens);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(store.AuthFilePath))!.AsObject();
        var accountId = root["tokens"]!["account_id"];
        Assert.NotNull(accountId);
        Assert.Equal("11111111-2222-3333-4444-555555555555", accountId!.GetValue<string>());
    }

    /// <summary>若令牌里确实没有账号 ID，不能凭空编造，也不能把它变成 null 以外的东西。</summary>
    [Fact]
    public async Task SaveAsync_PreservesExistingAccountIdWhenTokenLacksOne()
    {
        using var profile = new TemporaryDirectory();
        using var store = new CodexAuthStore(profile.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.AuthFilePath)!);
        await File.WriteAllTextAsync(store.AuthFilePath, """
            { "tokens": { "account_id": "existing-account-id" } }
            """, new UTF8Encoding(false));

        // 该 JWT 没有 auth 命名空间。
        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64Url("{\"email\":\"user@example.test\"}");
        var tokens = new TokenSet
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            IdToken = $"{header}.{payload}.sig"
        };

        await store.SaveAsync(tokens);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(store.AuthFilePath))!.AsObject();
        Assert.Equal("existing-account-id", root["tokens"]!["account_id"]!.GetValue<string>());
    }

    private static string CreateUnsignedJwt(string email, string accountId)
    {
        // 真实 id_token 把 chatgpt_account_id 放在 https://api.openai.com/auth 命名空间内，
        // 而不是顶层。测试必须用真实结构，否则会掩盖账号 ID 写不进去的问题。
        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64Url(
            $"{{\"email\":\"{email}\",\"https://api.openai.com/auth\":{{\"chatgpt_account_id\":\"{accountId}\"}}}}");
        return $"{header}.{payload}.fixture-signature";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
