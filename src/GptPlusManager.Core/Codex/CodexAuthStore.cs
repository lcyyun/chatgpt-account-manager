using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Codex;

public sealed class CodexAuthStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CodexAuthStore(string? userProfile = null)
    {
        var profile = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile;
        AuthFilePath = Path.Combine(profile, ".codex", "auth.json");
    }

    public string AuthFilePath { get; }

    public async Task<TokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetCurrentEmailAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false))?.Email;

    public async Task<bool> IsCurrentEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentEmailAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(email)
            && string.Equals(current, email, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        tokens.NormalizeStrings();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(AuthFilePath)!;
            Directory.CreateDirectory(directory);
            JsonObject root;
            if (File.Exists(AuthFilePath))
            {
                var existing = await File.ReadAllTextAsync(AuthFilePath, cancellationToken).ConfigureAwait(false);
                root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            root["auth_mode"] ??= "chatgpt";
            if (!root.ContainsKey("OPENAI_API_KEY"))
            {
                root["OPENAI_API_KEY"] = null;
            }

            var tokenNode = root["tokens"] as JsonObject ?? new JsonObject();
            tokenNode["id_token"] = tokens.IdToken;
            tokenNode["access_token"] = tokens.AccessToken;
            tokenNode["refresh_token"] = tokens.RefreshToken;
            tokenNode["account_id"] = JwtUtility.GetStringClaim(tokens.IdToken, "chatgpt_account_id");
            root["tokens"] = tokenNode;
            root["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");

            string? backupPath = null;
            if (File.Exists(AuthFilePath))
            {
                backupPath = $"{AuthFilePath}.{DateTime.Now:yyyyMMdd-HHmmssfff}.bak";
                File.Copy(AuthFilePath, backupPath, false);
            }

            var tempPath = Path.Combine(directory, $".auth.json.{Guid.NewGuid():N}.tmp");
            try
            {
                var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                _ = JsonNode.Parse(await File.ReadAllTextAsync(tempPath, cancellationToken).ConfigureAwait(false))
                    ?? throw new JsonException("Written auth.json could not be parsed.");
                if (File.Exists(AuthFilePath))
                {
                    File.Replace(tempPath, AuthFilePath, null, true);
                }
                else
                {
                    File.Move(tempPath, AuthFilePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            return backupPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> SwitchAccountAsync(TokenSet tokens, CancellationToken cancellationToken = default) =>
        await SaveAsync(tokens, cancellationToken).ConfigureAwait(false);

    private async Task<TokenSet?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AuthFilePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(AuthFilePath, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject;
        var tokenNode = root?["tokens"] as JsonObject;
        if (tokenNode is null)
        {
            return null;
        }

        var tokens = new TokenSet
        {
            AccessToken = tokenNode["access_token"]?.GetValue<string>() ?? string.Empty,
            RefreshToken = tokenNode["refresh_token"]?.GetValue<string>() ?? string.Empty,
            IdToken = tokenNode["id_token"]?.GetValue<string>() ?? string.Empty
        };
        tokens.Email = JwtUtility.GetStringClaim(tokens.IdToken, "email") ?? string.Empty;
        return string.IsNullOrEmpty(tokens.AccessToken) ? null : tokens;
    }

    public void Dispose() => _gate.Dispose();
}
