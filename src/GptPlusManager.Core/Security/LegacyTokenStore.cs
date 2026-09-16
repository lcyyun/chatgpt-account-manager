using System.Text;
using System.Text.Json;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Persistence;

namespace GptPlusManager.Core.Security;

public sealed class LegacyTokenStore : IDisposable
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LegacyTokenStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public LegacyTokenStore(string dataRoot) : this(new AppPaths(dataRoot))
    {
    }

    public string GetTokenPath(string email)
    {
        email ??= string.Empty;
        var safeName = email;
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(character, '_');
        }

        if (safeName.Length > 40)
        {
            safeName = safeName[..40];
        }

        uint hash = 2166136261;
        foreach (var character in email)
        {
            hash ^= character;
            hash = unchecked(hash * 16777619);
        }

        return Path.Combine(_paths.TokensDirectory, $"{safeName}-{hash:x8}.token");
    }

    public bool Exists(string email) => File.Exists(GetTokenPath(email));

    public async Task<TokenSet?> LoadAsync(string email, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetTokenPath(email);
            if (!File.Exists(path))
            {
                return null;
            }

            var base64 = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var protectedBytes = Convert.FromBase64String(base64.Trim());
            var json = Encoding.UTF8.GetString(WindowsDpapi.Unprotect(protectedBytes));
            var tokens = JsonSerializer.Deserialize<TokenSet>(json, AtomicJsonFile.Options);
            tokens?.NormalizeStrings();
            return tokens;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Legacy behavior treats unreadable, moved-user, or damaged DPAPI blobs as absent tokens.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string email, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        tokens.NormalizeStrings();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.TokensDirectory);
            var path = GetTokenPath(email);
            var json = JsonSerializer.Serialize(tokens, AtomicJsonFile.Options);
            var protectedBytes = WindowsDpapi.Protect(Encoding.UTF8.GetBytes(json));
            var payload = Convert.ToBase64String(protectedBytes);
            var tempPath = Path.Combine(
                _paths.TokensDirectory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(tempPath, payload, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                _ = WindowsDpapi.Unprotect(Convert.FromBase64String(
                    await File.ReadAllTextAsync(tempPath, cancellationToken).ConfigureAwait(false)));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null, true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
