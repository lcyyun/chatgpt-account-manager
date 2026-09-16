using GptPlusManager.Core.Models;

namespace GptPlusManager.Core.Persistence;

public sealed class SettingsRepository : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsRepository(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.SettingsFile;
    }

    public SettingsRepository(string dataRoot) : this(new AppPaths(dataRoot))
    {
    }

    public async Task<SettingsRecord> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await AtomicJsonFile.ReadAsync<SettingsRecord>(_path, cancellationToken)
                .ConfigureAwait(false) ?? new SettingsRecord();
            settings.Normalize();
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SettingsRecord settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(_path, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
