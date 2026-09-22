using GptPlusManager.Core.Persistence;

namespace GptPlusManager.Core.Codex;

/// <summary>
/// 第三方供应商定义的持久化（<c>providers.json</c>）。
/// 只存定义，<b>不存密钥</b>——密钥在 <see cref="ProviderSecrets"/> 里单独加密保存，
/// 这样这个文件可以随手备份或贴给别人而不泄密。
/// </summary>
public sealed class ProviderRegistry : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public ProviderRegistry(string? dataRoot = null)
    {
        var paths = new AppPaths(dataRoot);
        _filePath = Path.Combine(paths.DataRoot, "providers.json");
    }

    public string FilePath => _filePath;

    public async Task<IReadOnlyList<ProviderDefinition>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).Providers;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetActiveProviderIdAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(record.ActiveProviderId) ? null : record.ActiveProviderId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<ProviderDefinition> providers,
        string? activeProviderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var record = new ProviderRecord
        {
            Providers = [.. providers],
            ActiveProviderId = activeProviderId ?? string.Empty,
        };
        foreach (var provider in record.Providers) provider.Normalize();
        record.Providers = [.. record.Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())];

        // 指向一个不存在的供应商会让"当前生效"状态变成悬空引用——用户改了 ID 之后
        // 界面会把旧 ID 当成生效中的那个。宁可清空，也不留一个查不到的 ID。
        if (record.ActiveProviderId.Length > 0 &&
            !record.Providers.Any(p => string.Equals(p.Id, record.ActiveProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            record.ActiveProviderId = string.Empty;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(_filePath, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProviderRecord> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var record = await AtomicJsonFile.ReadAsync<ProviderRecord>(_filePath, cancellationToken)
            .ConfigureAwait(false) ?? new ProviderRecord();
        record.Providers ??= [];
        foreach (var provider in record.Providers) provider.Normalize();
        return record;
    }

    private sealed class ProviderRecord
    {
        public List<ProviderDefinition> Providers { get; set; } = [];
        public string ActiveProviderId { get; set; } = string.Empty;
    }

    public void Dispose() => _gate.Dispose();
}
