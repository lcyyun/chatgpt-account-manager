using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GptPlusManager.Wpf.Infrastructure;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf.ViewModels;

public sealed partial class AccountViewModel : ObservableObject
{
    private readonly IAccountManagerService _service;
    private readonly IUiService _ui;
    private readonly Func<AccountViewModel, Task> _editRequested;
    private readonly Func<AccountViewModel, Task> _deleteRequested;

    /// <summary>账号状态变化（如 Codex 标记）。只刷新显示，绝不改变列表顺序。</summary>
    private readonly Action<AccountViewModel> _stateChanged;

    /// <summary>账号有效性变化。需要把卡片移入对应的有效 / 无效分组。</summary>
    private readonly Action<AccountViewModel> _validityChanged;

    private readonly Action<string, bool> _toast;
    private IAccountAuthorizationSession? _authorizationSession;

    public AccountViewModel(AccountSnapshot s, IAccountManagerService service, IUiService ui,
        Func<AccountViewModel, Task> editRequested, Func<AccountViewModel, Task> deleteRequested,
        Action<AccountViewModel> stateChanged, Action<AccountViewModel> validityChanged, Action<string, bool> toast)
    {
        _service = service; _ui = ui; _editRequested = editRequested; _deleteRequested = deleteRequested;
        _stateChanged = stateChanged; _validityChanged = validityChanged; _toast = toast; Id = s.Id;
        AuthorizeCommand = new AsyncRelayCommand(StartAuthorizationAsync, () => !IsBusy && !IsAuthorizing);
        ReopenAuthorizationCommand = new RelayCommand(ReopenAuthorization, () => IsAuthorizing);
        CopyAuthorizationUrlCommand = new RelayCommand(CopyAuthorizationUrl, () => IsAuthorizing);
        CancelAuthorizationCommand = new RelayCommand(CancelAuthorization, () => IsAuthorizing);
        QueryUsageCommand = new AsyncRelayCommand(QueryUsageAsync, CanRun);
        EditCommand = new AsyncRelayCommand(() => _editRequested(this), CanRun);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, CanRun);
        SwitchCodexCommand = new AsyncRelayCommand(SwitchCodexAsync, CanRun);
        ConsumeResetCommand = new AsyncRelayCommand(ConsumeResetAsync, () => CanRun() && ResetCreditsAvailable > 0);
        ToggleInvalidCommand = new AsyncRelayCommand(ToggleInvalidAsync, CanRun);
        TogglePasswordCommand = new RelayCommand(() => IsPasswordVisible = !IsPasswordVisible);
        CopyEmailCommand = new RelayCommand(() => Copy(Email, "邮箱"));
        CopyPasswordCommand = new RelayCommand(() => Copy(Password, "密码"));
        CopySecretCommand = new RelayCommand(() => Copy(TwoFactorSecret, "2FA 密钥"));
        CopyCodeCommand = new AsyncRelayCommand(CopyCodeAsync);
        // 属性赋值必须在命令创建之后：变更处理器会通知命令刷新可用状态。
        Apply(s);
        Tick(DateTimeOffset.Now);
    }

    public Guid Id { get; }
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _twoFactorSecret = string.Empty;
    [ObservableProperty] private DateTimeOffset? _purchasedAt;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _planName = string.Empty;
    [ObservableProperty] private DateTimeOffset? _subscriptionUntil;
    [ObservableProperty] private bool _isAuthorized;
    [ObservableProperty] private bool _isInvalid;
    [ObservableProperty] private bool _isCurrentCodex;
    [ObservableProperty] private double _primaryUsagePercent;
    [ObservableProperty] private double _secondaryUsagePercent;
    [ObservableProperty] private string _primaryDescription = "5小时窗口";
    [ObservableProperty] private string _secondaryDescription = "每周窗口";
    [ObservableProperty] private DateTimeOffset? _primaryResetsAt;
    [ObservableProperty] private DateTimeOffset? _secondaryResetsAt;
    [ObservableProperty] private int _resetCreditsAvailable;
    [ObservableProperty] private bool _isPasswordVisible;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCodeCopied;
    [ObservableProperty] private bool _isAuthorizing;
    [ObservableProperty] private string _authorizationUrl = string.Empty;
    [ObservableProperty] private string _authorizationStatus = "等待浏览器完成登录";
    [ObservableProperty] private bool _hasPrimaryUsage;
    [ObservableProperty] private bool _hasSecondaryUsage;
    [ObservableProperty] private string _totpCode = "------";
    [ObservableProperty] private string _primaryResetText = string.Empty;
    [ObservableProperty] private string _secondaryResetText = string.Empty;

    public string PasswordMask => new('•', Math.Clamp(Password.Length, 8, 14));
    public string PurchasedText => PurchasedAt.HasValue ? $"购买于 {PurchasedAt:yyyy-MM-dd}" : "购买时间未记录";
    public string SubscriptionText => SubscriptionUntil.HasValue ? $"订阅至 {SubscriptionUntil:yyyy-MM-dd}" : "订阅时间未知";
    public string AuthorizationText => IsInvalid ? "账号无效" : IsAuthorized ? "授权有效" : "尚未授权";
    public string InvalidButtonText => IsInvalid ? "恢复有效" : "标记无效";
    public string ResetCreditText => $"重置卡 {ResetCreditsAvailable} 张";
    public double PrimaryRemaining => Math.Max(0, 100 - PrimaryUsagePercent);
    public double SecondaryRemaining => Math.Max(0, 100 - SecondaryUsagePercent);

    public IAsyncRelayCommand AuthorizeCommand { get; }
    public IRelayCommand ReopenAuthorizationCommand { get; }
    public IRelayCommand CopyAuthorizationUrlCommand { get; }
    public IRelayCommand CancelAuthorizationCommand { get; }
    public IAsyncRelayCommand QueryUsageCommand { get; }
    public IAsyncRelayCommand EditCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand SwitchCodexCommand { get; }
    public IAsyncRelayCommand ConsumeResetCommand { get; }
    public IAsyncRelayCommand ToggleInvalidCommand { get; }
    public IRelayCommand TogglePasswordCommand { get; }
    public IRelayCommand CopyEmailCommand { get; }
    public IRelayCommand CopyPasswordCommand { get; }
    public IRelayCommand CopySecretCommand { get; }
    public IAsyncRelayCommand CopyCodeCommand { get; }

    partial void OnPasswordChanged(string value) => OnPropertyChanged(nameof(PasswordMask));
    partial void OnIsInvalidChanged(bool value) { OnPropertyChanged(nameof(AuthorizationText)); OnPropertyChanged(nameof(InvalidButtonText)); }
    partial void OnIsAuthorizedChanged(bool value) => OnPropertyChanged(nameof(AuthorizationText));
    partial void OnResetCreditsAvailableChanged(int value) { OnPropertyChanged(nameof(ResetCreditText)); ConsumeResetCommand.NotifyCanExecuteChanged(); }
    partial void OnPrimaryUsagePercentChanged(double value) => OnPropertyChanged(nameof(PrimaryRemaining));
    partial void OnSecondaryUsagePercentChanged(double value) => OnPropertyChanged(nameof(SecondaryRemaining));
    partial void OnPurchasedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(PurchasedText));
    partial void OnSubscriptionUntilChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(SubscriptionText));
    partial void OnIsAuthorizingChanged(bool value)
    {
        AuthorizeCommand.NotifyCanExecuteChanged();
        ReopenAuthorizationCommand.NotifyCanExecuteChanged();
        CopyAuthorizationUrlCommand.NotifyCanExecuteChanged();
        CancelAuthorizationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        AuthorizeCommand.NotifyCanExecuteChanged(); QueryUsageCommand.NotifyCanExecuteChanged(); EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged(); SwitchCodexCommand.NotifyCanExecuteChanged(); ConsumeResetCommand.NotifyCanExecuteChanged(); ToggleInvalidCommand.NotifyCanExecuteChanged();
    }

    public void Tick(DateTimeOffset now)
    {
        TotpCode = _service.GenerateTotp(TwoFactorSecret, now);
        PrimaryResetText = FormatRemaining(PrimaryResetsAt, now); SecondaryResetText = FormatRemaining(SecondaryResetsAt, now);
    }

    public AccountEditRequest ToEditRequest() => new(Email, Password, TwoFactorSecret, PurchasedAt, Note);
    public async Task ApplyEditAsync(AccountEditRequest request) => await RunAsync(async () => Apply(await _service.UpdateAccountAsync(Id, request)));
    public async Task<bool> QueryUsageFromBatchAsync() { try { await QueryUsageCoreAsync(); return true; } catch (Exception ex) { _toast($"{Email}: {ex.Message}", true); return false; } }

    private bool CanRun() => !IsBusy && !IsAuthorizing;

    private async Task StartAuthorizationAsync()
    {
        if (IsAuthorizing) return;
        try
        {
            _authorizationSession?.Dispose();
            _authorizationSession = _service.StartAuthorization(Id);
            AuthorizationUrl = _authorizationSession.AuthorizationUri.AbsoluteUri;
            AuthorizationStatus = "等待浏览器完成登录";
            IsAuthorizing = true;
            var result = await _authorizationSession.Completion;
            IsAuthorized = result.IsAuthorized;
            IsInvalid = result.IsInvalid;
            IsCurrentCodex = result.IsCurrentCodex;
            _validityChanged(this);
            _toast($"{Email}: {result.StatusText}", false);
        }
        catch (OperationCanceledException)
        {
            AuthorizationStatus = "授权已取消";
            _toast("授权已取消", false);
        }
        catch (Exception ex)
        {
            AuthorizationStatus = ex.Message;
            _toast(ex.Message, true);
        }
        finally
        {
            IsAuthorizing = false;
            _authorizationSession?.Dispose();
            _authorizationSession = null;
        }
    }

    private void ReopenAuthorization()
    {
        if (_authorizationSession?.Reopen() == true) AuthorizationStatus = "已重新打开浏览器，等待登录";
        else _toast("无法打开默认浏览器，请复制登录链接后手动打开", true);
    }

    private void CopyAuthorizationUrl()
    {
        if (AuthorizationUrl.Length == 0) return;
        _ui.CopyText(AuthorizationUrl);
        _toast("登录链接已复制", false);
    }

    private void CancelAuthorization()
    {
        _authorizationSession?.Cancel();
        AuthorizationStatus = "正在取消…";
    }
    private async Task QueryUsageAsync() => await RunAsync(QueryUsageCoreAsync);
    private async Task QueryUsageCoreAsync() { Apply(await _service.QueryUsageAsync(Id)); Tick(DateTimeOffset.Now); _toast($"已更新 {Email} 的用量", false); }
    private async Task DeleteAsync() => await RunAsync(() => _deleteRequested(this));
    private async Task SwitchCodexAsync() => await RunAsync(async () => { await _service.SwitchCodexAsync(Id); IsCurrentCodex = true; _stateChanged(this); _toast($"Codex 已切换到 {Email}", false); });
    private async Task ConsumeResetAsync() => await RunAsync(async () => { if (!_ui.Confirm("兑换重置卡", $"对 {Email} 使用一张重置卡？两个用量窗口会被重置。")) return; var r = await _service.ConsumeResetCreditAsync(Id); ResetCreditsAvailable = r.AvailableCredits; await QueryUsageCoreAsync(); _toast(r.Message, false); });
    private async Task ToggleInvalidAsync() => await RunAsync(async () => { Apply(await _service.SetInvalidAsync(Id, !IsInvalid)); _validityChanged(this); _toast(IsInvalid ? "账号已标记无效" : "账号已恢复有效", false); });
    private void Copy(string value, string label) { if (string.IsNullOrEmpty(value)) return; _ui.CopyText(value); _toast($"已复制{label}", false); }
    private async Task CopyCodeAsync() { _ui.CopyText(TotpCode); IsCodeCopied = true; _toast("验证码已复制", false); await Task.Delay(650); IsCodeCopied = false; }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true;
        try { await action(); } catch (Exception ex) { _toast(ex.Message, true); }
        finally { IsBusy = false; }
    }

    /// <summary>用最新快照刷新显示（例如 Codex 当前账号在别处被切换后）。</summary>
    public void RefreshFromSnapshot(AccountSnapshot snapshot) => Apply(snapshot);

    private void Apply(AccountSnapshot s)
    {
        Email = s.Email; Password = s.Password; TwoFactorSecret = s.TwoFactorSecret; PurchasedAt = s.PurchasedAt; Note = s.Note;
        PlanName = s.PlanName; SubscriptionUntil = s.SubscriptionUntil; IsAuthorized = s.IsAuthorized; IsInvalid = s.IsInvalid; IsCurrentCodex = s.IsCurrentCodex;
        HasPrimaryUsage = s.HasPrimaryUsage; HasSecondaryUsage = s.HasSecondaryUsage;
        PrimaryUsagePercent = s.PrimaryUsagePercent; SecondaryUsagePercent = s.SecondaryUsagePercent; PrimaryDescription = string.IsNullOrWhiteSpace(s.PrimaryDescription) ? "5小时窗口" : s.PrimaryDescription;
        SecondaryDescription = string.IsNullOrWhiteSpace(s.SecondaryDescription) ? "每周窗口" : s.SecondaryDescription; PrimaryResetsAt = s.PrimaryResetsAt; SecondaryResetsAt = s.SecondaryResetsAt; ResetCreditsAvailable = s.ResetCreditsAvailable;
    }

    private void Apply(UsageSnapshot s)
    {
        PlanName = s.PlanName; HasPrimaryUsage = s.HasPrimaryUsage; HasSecondaryUsage = s.HasSecondaryUsage;
        PrimaryUsagePercent = s.PrimaryPercent; SecondaryUsagePercent = s.SecondaryPercent;
        PrimaryDescription = string.IsNullOrWhiteSpace(s.PrimaryDescription) ? "5小时窗口" : s.PrimaryDescription; SecondaryDescription = string.IsNullOrWhiteSpace(s.SecondaryDescription) ? "每周窗口" : s.SecondaryDescription;
        PrimaryResetsAt = s.PrimaryResetsAt; SecondaryResetsAt = s.SecondaryResetsAt; SubscriptionUntil = s.SubscriptionUntil; ResetCreditsAvailable = s.ResetCreditsAvailable; IsInvalid = s.IsInvalid;
    }

    private static string FormatRemaining(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (!resetAt.HasValue) return "重置时间未知"; var r = resetAt.Value - now;
        if (r <= TimeSpan.Zero) return "已到重置时间"; if (r.TotalDays >= 1) return $"{(int)r.TotalDays}天 {r.Hours}小时后";
        return $"{r.Hours:00}:{r.Minutes:00}:{r.Seconds:00} 后";
    }
}
