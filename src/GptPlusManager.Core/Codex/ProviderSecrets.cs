using System.Text;
using System.Text.Json;
using GptPlusManager.Core.Persistence;
using GptPlusManager.Core.Security;

namespace GptPlusManager.Core.Codex;

/// <summary>
/// 第三方供应商 API Key 的本地加密存储。
///
/// 用 Windows DPAPI（CurrentUser 作用域）加密，密文只能被<b>当前 Windows 用户</b>解密，
/// 拷到别的机器或别的账户上都是废数据。明文密钥从不落盘，也从不写进 config.toml
/// （命令式配方下）。
/// </summary>
public sealed class ProviderSecrets : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public ProviderSecrets(string? dataRoot = null)
    {
        var paths = new AppPaths(dataRoot);
        _filePath = Path.Combine(paths.DataRoot, "provider-secrets.json");
    }

    public string FilePath => _filePath;

    public async Task SetAsync(string providerId, string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("供应商 ID 不能为空。", nameof(providerId));
        ArgumentNullException.ThrowIfNull(apiKey);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            // 允许保存空串（表示"清空"），但空串不加密。
            record.Keys[providerId] = apiKey.Length == 0
                ? string.Empty
                : Convert.ToBase64String(WindowsDpapi.Protect(Encoding.UTF8.GetBytes(apiKey)));
            await AtomicJsonFile.WriteAsync(_filePath, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!record.Keys.TryGetValue(providerId, out var encoded) || string.IsNullOrEmpty(encoded))
            {
                return null;
            }

            try
            {
                var plain = WindowsDpapi.Unprotect(Convert.FromBase64String(encoded));
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception exception) when (exception is FormatException or System.ComponentModel.Win32Exception)
            {
                // 换了机器或换了 Windows 账户后 DPAPI 解不开——当作"没有密钥"处理，
                // 让用户重新填一次，而不是抛异常卡住启动。
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (record.Keys.Remove(providerId))
            {
                await AtomicJsonFile.WriteAsync(_filePath, record, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasAsync(string providerId, CancellationToken cancellationToken = default) =>
        !string.IsNullOrEmpty(await GetAsync(providerId, cancellationToken).ConfigureAwait(false));

    private async Task<SecretRecord> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var record = await AtomicJsonFile.ReadAsync<SecretRecord>(_filePath, cancellationToken)
            .ConfigureAwait(false) ?? new SecretRecord();
        record.Keys ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return record;
    }

    private sealed class SecretRecord
    {
        public Dictionary<string, string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => _gate.Dispose();
}
