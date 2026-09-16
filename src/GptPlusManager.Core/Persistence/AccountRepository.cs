using GptPlusManager.Core.Models;

namespace GptPlusManager.Core.Persistence;

public sealed class AccountRepository : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountRepository(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.AccountsFile;
    }

    public AccountRepository(string dataRoot) : this(new AppPaths(dataRoot))
    {
    }

    public async Task<IReadOnlyList<AccountRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = await AtomicJsonFile.ReadAsync<List<AccountRecord>>(_path, cancellationToken)
                .ConfigureAwait(false) ?? [];
            foreach (var account in accounts)
            {
                account.NormalizeStrings();
            }

            return accounts;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<AccountRecord> accounts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var snapshot = accounts.ToList();
        foreach (var account in snapshot)
        {
            ArgumentNullException.ThrowIfNull(account);
            account.NormalizeStrings();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(_path, snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
