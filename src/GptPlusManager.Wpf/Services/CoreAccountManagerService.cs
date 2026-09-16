using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using GptPlusManager.Core.Codex;
using GptPlusManager.Core.Models;
using GptPlusManager.Core.Network;
using GptPlusManager.Core.Persistence;
using GptPlusManager.Core.Security;
using GptPlusManager.Core.Services;
using CoreUsageSnapshot = GptPlusManager.Core.Models.UsageSnapshot;

namespace GptPlusManager.Wpf.Services;

public sealed class CoreAccountManagerService : IAccountManagerService
{
    private readonly AppPaths _paths;
    private readonly AccountRepository _accounts;
    private readonly SettingsRepository _settings;
    private readonly LegacyTokenStore _tokens;
    private readonly CodexAuthStore _codex;
    private readonly HttpClient _http;
    private readonly AuthClient _auth;
    private readonly UsageClient _usage;
    private readonly ClientProcessService _processes = new();
    private readonly TotpService _totp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<AccountRecord> _records = [];
    private readonly Dictionary<Guid, AccountRecord> _byId = [];

    public CoreAccountManagerService(string dataRoot)
    {
        _paths = new AppPaths(dataRoot);
        _accounts = new AccountRepository(_paths);
        _settings = new SettingsRepository(_paths);
        _tokens = new LegacyTokenStore(_paths);
        _codex = new CodexAuthStore();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _auth = new AuthClient(_http, _tokens, _codex);
        _usage = new UsageClient(_http, _auth);
    }

    public async Task<IReadOnlyList<AccountSnapshot>> LoadAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _records = (await _accounts.LoadAsync(cancellationToken)).ToList();
            _byId.Clear();
            foreach (var record in _records) _byId[StableId(record)] = record;
            var currentEmail = await _codex.GetCurrentEmailAsync(cancellationToken);
            return _records.Select(x => ToSnapshot(x, currentEmail)).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountSnapshot> AddAccountAsync(AccountEditRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = new AccountRecord { CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            ApplyEdit(record, request);
            _records.Add(record);
            var id = StableId(record);
            _byId[id] = record;
            await _accounts.SaveAsync(_records, cancellationToken);
            return ToSnapshot(record, await _codex.GetCurrentEmailAsync(cancellationToken));
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountSnapshot> UpdateAccountAsync(Guid accountId, AccountEditRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = Get(accountId);
            var oldEmail = record.Email;
            ApplyEdit(record, request);
            if (!string.Equals(oldEmail, record.Email, StringComparison.OrdinalIgnoreCase))
            {
                var oldToken = await _tokens.LoadAsync(oldEmail, cancellationToken);
                if (oldToken is not null) await _tokens.SaveAsync(record.Email, oldToken, cancellationToken);
                // Keep the current session ID stable. On next launch it is deterministically rebuilt
                // from the persisted account, while commands already bound to this card remain valid now.
                _byId[accountId] = record;
            }
            await _accounts.SaveAsync(_records, cancellationToken);
            return ToSnapshot(record, await _codex.GetCurrentEmailAsync(cancellationToken));
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = Get(accountId);
            _records.Remove(record);
            _byId.Remove(accountId);
            await _accounts.SaveAsync(_records, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveOrderAsync(IReadOnlyList<Guid> accountIds, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var ordered = accountIds.Where(_byId.ContainsKey).Select(id => _byId[id]).ToList();
            ordered.AddRange(_records.Where(x => !ordered.Contains(x)));
            _records = ordered;
            await _accounts.SaveAsync(_records, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public IAccountAuthorizationSession StartAuthorization(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var record = Get(accountId);
        var session = _auth.StartAuthorizationSession(
            openSystemBrowser: true,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);
        return new AccountAuthorizationSession(this, record, session);
    }

    private async Task<AuthResult> CompleteAuthorizationAsync(
        AccountRecord record,
        IAuthorizationSession session)
    {
        var result = await session.Completion.ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(Localize(result));
        }

        var token = result.Value!.Tokens;
        if (!string.IsNullOrWhiteSpace(token.Email)
            && !string.Equals(token.Email, record.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"浏览器登录的是 {token.Email}，与卡片账号不一致。");
        }

        await _auth.SaveTokensAsync(record.Email, token).ConfigureAwait(false);
        record.IsInvalid = false;
        await SaveRecordsAsync(CancellationToken.None).ConfigureAwait(false);
        return new AuthResult(
            true,
            false,
            "授权成功",
            await _codex.IsCurrentEmailAsync(record.Email).ConfigureAwait(false));
    }

    public async Task<UsageSnapshot> QueryUsageAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var record = Get(accountId);
        var result = await _usage.GetUsageAsync(record, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(Localize(result));
        result.Value!.ApplyTo(record);
        await SaveRecordsAsync(cancellationToken);
        return ToUiUsage(record, result.Value);
    }

    public async Task<ResetCreditUiResult> ConsumeResetCreditAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var record = Get(accountId);
        var result = await _usage.ConsumeResetCreditAsync(record, cancellationToken);
        if (result.Value is null) throw new InvalidOperationException(Localize(result));
        record.ResetCreditsAvailable = result.Value.AvailableCredits;
        await SaveRecordsAsync(cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(Localize(result));
        var refreshed = await QueryUsageAsync(accountId, cancellationToken);
        return new(result.Value.Code, result.Value.WindowsReset, refreshed.ResetCreditsAvailable,
            $"已重置 {result.Value.WindowsReset} 个用量窗口");
    }

    public async Task SwitchCodexAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var record = Get(accountId);
        var tokenResult = await _auth.GetValidTokensAsync(record, cancellationToken: cancellationToken);
        if (!tokenResult.Succeeded) throw new InvalidOperationException(Localize(tokenResult));
        await _codex.SwitchAccountAsync(tokenResult.Value!, cancellationToken);
        foreach (var item in _records) item.CodexSwitchedAt = string.Empty;
        record.CodexSwitchedAt = DateTime.Now.ToString("MM-dd HH:mm");
        await SaveRecordsAsync(cancellationToken);
    }

    public async Task<AccountSnapshot> SetInvalidAsync(Guid accountId, bool isInvalid, CancellationToken cancellationToken = default)
    {
        var record = Get(accountId);
        record.IsInvalid = isInvalid;
        await SaveRecordsAsync(cancellationToken);
        return ToSnapshot(record, await _codex.GetCurrentEmailAsync(cancellationToken));
    }

    public async Task BackupJsonAsync(CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_paths.DataRoot, "exports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"accounts-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await using var source = File.OpenRead(_paths.AccountsFile);
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
    }

    public async Task ExportTextAsync(CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_paths.DataRoot, "exports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"accounts-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllLinesAsync(path, _records.Select(x => x.DisplayLine), new UTF8Encoding(false), cancellationToken);
    }

    public async Task<bool> GetKeepAliveAsync(CancellationToken cancellationToken = default) =>
        (await _settings.LoadAsync(cancellationToken)).Enabled;

    public async Task SetKeepAliveAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        settings.Enabled = enabled;
        await _settings.SaveAsync(settings, cancellationToken);
    }

    public Task RestartCodexAsync(CancellationToken cancellationToken = default) =>
        _processes.RestartAsync(cancellationToken: cancellationToken);

    public string GenerateTotp(string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || !_totp.IsValidSecret(secret)) return "------";
        return _totp.GetCurrentCode(secret, now).Code;
    }

    private async Task SaveRecordsAsync(CancellationToken cancellationToken) => await _accounts.SaveAsync(_records, cancellationToken);
    private AccountRecord Get(Guid id) => _byId.TryGetValue(id, out var record) ? record : throw new KeyNotFoundException("账号不存在或已刷新。");

    private static void Validate(AccountEditRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email)) throw new InvalidOperationException("邮箱不能为空。");
    }

    private void ApplyEdit(AccountRecord target, AccountEditRequest request)
    {
        target.Email = request.Email.Trim();
        target.Password = request.Password;
        target.Secret = string.IsNullOrWhiteSpace(request.TwoFactorSecret) ? string.Empty : _totp.NormalizeBase32(request.TwoFactorSecret);
        if (target.Secret.Length > 0 && !_totp.IsValidSecret(target.Secret)) throw new InvalidOperationException("2FA 密钥格式不正确。");
        target.PurchasedAt = request.PurchasedAt?.ToString("yyyy-MM-dd") ?? string.Empty;
        target.Note = request.Note.Trim();
    }

    private AccountSnapshot ToSnapshot(AccountRecord x, string? currentEmail) => new(
        StableId(x), x.Email, x.Password, x.Secret, ParseDate(x.PurchasedAt), x.Note, x.UsagePlan,
        FromUnix(x.SubscriptionUntilUnix), _tokens.Exists(x.Email), x.IsInvalid,
        string.Equals(x.Email, currentEmail, StringComparison.OrdinalIgnoreCase),
        x.UsageP1 >= 0, x.UsageP2 >= 0,
        Positive(x.UsageP1), Positive(x.UsageP2), x.UsageP1Desc, x.UsageP2Desc,
        FromUnix(x.UsageP1ResetAt), FromUnix(x.UsageP2ResetAt), x.ResetCreditsAvailable, x.UsageCheckedAt);

    private static UsageSnapshot ToUiUsage(AccountRecord account, CoreUsageSnapshot snapshot) => new(
        snapshot.PlanType, snapshot.Primary is not null, snapshot.Secondary is not null,
        Positive(account.UsageP1), Positive(account.UsageP2), account.UsageP1Desc,
        account.UsageP2Desc, FromUnix(account.UsageP1ResetAt), FromUnix(account.UsageP2ResetAt),
        snapshot.SubscriptionUntil, account.ResetCreditsAvailable, account.IsInvalid);

    private static double Positive(double value) => value < 0 ? 0 : Math.Clamp(value, 0, 100);
    private static DateTimeOffset? FromUnix(double value) => value > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime() : null;
    private static DateTimeOffset? ParseDate(string value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    private static Guid StableId(AccountRecord record)
    {
        var key = string.IsNullOrWhiteSpace(record.CreatedAt) ? record.Email : record.CreatedAt + "|" + record.Email;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
    private static string Localize<T>(OperationResult<T> result) => result.ErrorCode switch
    {
        "not_authorized" => "该账号尚未授权，请先授权登录。",
        "network_error" => "网络连接失败：" + result.Message,
        "request_timeout" => "请求超时，请稍后重试。",
        "no_credit" => "该账号没有可用重置卡。",
        "nothing_to_reset" => "当前没有需要重置的已用额度。",
        _ => result.Message
    };

    private sealed class AccountAuthorizationSession : IAccountAuthorizationSession
    {
        private readonly IAuthorizationSession _session;

        public AccountAuthorizationSession(
            CoreAccountManagerService owner,
            AccountRecord record,
            IAuthorizationSession session)
        {
            _session = session;
            Completion = owner.CompleteAuthorizationAsync(record, session);
        }

        public Uri AuthorizationUri => _session.AuthorizationUri;
        public Task<AuthResult> Completion { get; }
        public bool Reopen() => _session.OpenSystemBrowser();
        public void Cancel() => _session.Cancel();
        public void Dispose() => _session.Dispose();
    }

    public void Dispose()
    {
        _usage.ToString();
        _auth.Dispose(); _codex.Dispose(); _tokens.Dispose(); _accounts.Dispose(); _settings.Dispose(); _http.Dispose(); _gate.Dispose();
    }
}
