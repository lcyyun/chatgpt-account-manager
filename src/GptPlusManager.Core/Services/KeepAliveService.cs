using GptPlusManager.Core.Models;
using GptPlusManager.Core.Network;
using GptPlusManager.Core.Persistence;

namespace GptPlusManager.Core.Services;

public sealed record KeepAliveRunResult(
    int Attempted,
    int Succeeded,
    int Failed,
    IReadOnlyDictionary<string, OperationResult<UsageSnapshot>> Results,
    DateTimeOffset CompletedAt);

public sealed class KeepAliveService
{
    private readonly UsageClient _usageClient;
    private readonly AccountRepository _accountRepository;
    private readonly SettingsRepository _settingsRepository;
    private readonly Random _random;

    public KeepAliveService(
        UsageClient usageClient,
        AccountRepository accountRepository,
        SettingsRepository settingsRepository,
        Random? random = null)
    {
        _usageClient = usageClient ?? throw new ArgumentNullException(nameof(usageClient));
        _accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _random = random ?? Random.Shared;
    }

    public DateTimeOffset CalculateNextRun(
        SettingsRecord settings,
        DateTimeOffset? now = null,
        TimeSpan? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        var current = now ?? DateTimeOffset.UtcNow;
        var jitterRange = jitter ?? TimeSpan.FromHours(1.5);
        var jitterSeconds = (_random.NextDouble() * 2 - 1) * jitterRange.TotalSeconds;
        var next = current.AddHours(settings.IntervalHours).AddSeconds(jitterSeconds);
        return next < current.AddMinutes(5) ? current.AddMinutes(5) : next;
    }

    public async Task<KeepAliveRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var accounts = (await _accountRepository.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var results = new Dictionary<string, OperationResult<UsageSnapshot>>(StringComparer.OrdinalIgnoreCase);
        var attempted = 0;
        var succeeded = 0;

        foreach (var account in accounts.Where(account => !account.IsInvalid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _usageClient.GetUsageAsync(account, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded && result.ErrorCode == "not_authorized")
            {
                continue;
            }

            attempted++;
            results[account.Email] = result;
            if (result.Succeeded)
            {
                result.Value!.ApplyTo(account);
                succeeded++;
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        if (attempted > 0)
        {
            await _accountRepository.SaveAsync(accounts, cancellationToken).ConfigureAwait(false);
        }

        var settings = await _settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        settings.LastKeepAlive = completedAt.ToUnixTimeSeconds();
        await _settingsRepository.SaveAsync(settings, cancellationToken).ConfigureAwait(false);

        return new KeepAliveRunResult(
            attempted,
            succeeded,
            attempted - succeeded,
            results,
            completedAt);
    }
}
