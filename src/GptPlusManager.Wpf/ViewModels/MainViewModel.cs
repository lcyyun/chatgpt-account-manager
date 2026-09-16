using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GptPlusManager.Core.Services;
using GptPlusManager.Wpf.Infrastructure;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAccountManagerService _service;
    private readonly IUiService _ui;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _toastCancellation;

    public MainViewModel(IAccountManagerService service, IUiService ui)
    {
        _service = service;
        _ui = ui;
        AccountsView = CollectionViewSource.GetDefaultView(Accounts);
        AccountsView.Filter = FilterAccount;
        InitializeCommand = new AsyncRelayCommand(InitializeAsync, () => !IsBusy);
        AddAccountCommand = new AsyncRelayCommand(AddAccountAsync, () => !IsBusy);
        BackupJsonCommand = new AsyncRelayCommand(BackupJsonAsync, () => !IsBusy);
        ExportTextCommand = new AsyncRelayCommand(ExportTextAsync, () => !IsBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        OpenExportsFolderCommand = new RelayCommand(OpenExportsFolder);
        QueryAllCommand = new AsyncRelayCommand(QueryAllAsync, () => !IsBusy && Accounts.Count > 0);
        RestartCodexCommand = new AsyncRelayCommand(RestartCodexAsync, () => !IsBusy);
        ToggleKeepAliveCommand = new AsyncRelayCommand(ToggleKeepAliveAsync, () => !IsBusy);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ThemeLabel = ThemeService.ThemeLabel;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnClockTick, Dispatcher.CurrentDispatcher);
        _clock.Start();
        OnClockTick(this, EventArgs.Empty);
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];
    public ICollectionView AccountsView { get; }
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSavingOrder;
    [ObservableProperty] private bool _isKeepAliveEnabled = true;
    [ObservableProperty] private string _globalTotpCountdown = "30s";
    [ObservableProperty] private string _themeLabel = string.Empty;
    [ObservableProperty] private int _globalTotpRemaining = 30;
    [ObservableProperty] private string _statusText = "正在读取现有账号";
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private bool _isToastError;
    [ObservableProperty] private string _toastMessage = string.Empty;

    public bool CanReorder => string.IsNullOrWhiteSpace(SearchText) && !IsBusy && !IsSavingOrder;
    public int VisibleAccountCount => AccountsView.Cast<object>().Count();
    public int InvalidAccountCount => Accounts.Count(x => x.IsInvalid);
    public string KeepAliveText => IsKeepAliveEnabled ? "保活：开" : "保活：关";

    /// <summary>导出目录，显示在状态栏并提供一键打开。</summary>
    public string ExportsDirectory => _service.ExportsDirectory;
    public IAsyncRelayCommand InitializeCommand { get; }
    public IAsyncRelayCommand AddAccountCommand { get; }
    public IAsyncRelayCommand BackupJsonCommand { get; }
    public IAsyncRelayCommand ExportTextCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IRelayCommand OpenExportsFolderCommand { get; }
    public IAsyncRelayCommand QueryAllCommand { get; }
    public IAsyncRelayCommand RestartCodexCommand { get; }
    public IAsyncRelayCommand ToggleKeepAliveCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }

    partial void OnSearchTextChanged(string value) { AccountsView.Refresh(); NotifyCounts(); }
    partial void OnIsBusyChanged(bool value)
    {
        InitializeCommand.NotifyCanExecuteChanged(); AddAccountCommand.NotifyCanExecuteChanged();
        BackupJsonCommand.NotifyCanExecuteChanged(); ExportTextCommand.NotifyCanExecuteChanged();
        QueryAllCommand.NotifyCanExecuteChanged(); RestartCodexCommand.NotifyCanExecuteChanged();
        ToggleKeepAliveCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanReorder));
    }

    partial void OnIsKeepAliveEnabledChanged(bool value) => OnPropertyChanged(nameof(KeepAliveText));
    partial void OnIsSavingOrderChanged(bool value) => OnPropertyChanged(nameof(CanReorder));

    public AccountViewModel[] CaptureOrder() => Accounts.ToArray();

    public int GetGroupStart(bool invalid) => invalid ? Accounts.Count(x => !x.IsInvalid) : 0;
    public int GetGroupEnd(bool invalid) => invalid ? Accounts.Count : Accounts.Count(x => !x.IsInvalid);

    public int PreviewMove(AccountViewModel source, int insertionBoundary)
    {
        if (!CanReorder) return Accounts.IndexOf(source);
        var oldIndex = Accounts.IndexOf(source);
        if (oldIndex < 0) return oldIndex;
        var min = GetGroupStart(source.IsInvalid);
        var maxBoundary = GetGroupEnd(source.IsInvalid);
        insertionBoundary = Math.Clamp(insertionBoundary, min, maxBoundary);
        var newIndex = AccountOrdering.ResolveMoveIndex(oldIndex, insertionBoundary, min, maxBoundary);
        if (newIndex != oldIndex) Accounts.Move(oldIndex, newIndex);
        return newIndex;
    }

    public void RestoreOrder(IReadOnlyList<AccountViewModel> original)
    {
        for (var desired = 0; desired < original.Count; desired++)
        {
            var current = Accounts.IndexOf(original[desired]);
            if (current >= 0 && current != desired) Accounts.Move(current, desired);
        }
    }

    public async Task CommitOrderAsync(IReadOnlyList<AccountViewModel> original)
    {
        if (IsSavingOrder) return;
        IsSavingOrder = true;
        try
        {
            await _service.SaveOrderAsync(Accounts.Select(x => x.Id).ToArray());
            ShowToast("账号顺序已保存", false);
        }
        catch (Exception ex)
        {
            RestoreOrder(original);
            ShowToast(ex.Message, true);
        }
        finally { IsSavingOrder = false; }
    }

    public async Task MoveAccountAsync(AccountViewModel source, AccountViewModel target)
    {
        if (!CanReorder || ReferenceEquals(source, target) || source.IsInvalid != target.IsInvalid) return;
        var original = CaptureOrder();
        var targetIndex = Accounts.IndexOf(target);
        PreviewMove(source, targetIndex);
        await CommitOrderAsync(original);
    }

    public void ShowToast(string message, bool isError)
    {
        _toastCancellation?.Cancel(); _toastCancellation = new CancellationTokenSource();
        ToastMessage = message; IsToastError = isError; IsToastVisible = true;
        _ = HideToastAsync(_toastCancellation.Token);
    }

    private async Task InitializeAsync()
    {
        await RunGlobalAsync(async ct =>
        {
            Accounts.Clear();
            foreach (var snapshot in await _service.LoadAccountsAsync(ct)) Accounts.Add(CreateAccount(snapshot));
            NormalizeValidityGroups();
            IsKeepAliveEnabled = await _service.GetKeepAliveAsync(ct);
            OnClockTick(this, EventArgs.Empty); NotifyCounts();
            StatusText = $"{Accounts.Count} 个账号 · 数据已从现有目录加载";
            QueryAllCommand.NotifyCanExecuteChanged();
        }, null);
    }

    private async Task AddAccountAsync()
    {
        var request = AccountEditorWindow.ShowDialog(null);
        if (request is null) return;
        await RunGlobalAsync(async ct =>
        {
            Accounts.Add(CreateAccount(await _service.AddAccountAsync(request, ct)));
            NotifyCounts(); ShowToast("账号已添加", false);
        }, null);
    }

    private async Task BackupJsonAsync()
    {
        await RunGlobalAsync(async ct =>
        {
            var path = await _service.BackupJsonAsync(ct);
            StatusText = "最近导出：" + path;
            ShowToast("JSON 备份已保存到 " + Path.GetFileName(path), false);
            _ui.RevealInExplorer(path);
        }, null);
    }

    private async Task ExportTextAsync()
    {
        await RunGlobalAsync(async ct =>
        {
            var path = await _service.ExportTextAsync(ct);
            StatusText = "最近导出：" + path;
            ShowToast("文本清单已保存到 " + Path.GetFileName(path), false);
            _ui.RevealInExplorer(path);
        }, null);
    }

    private async Task ImportAsync()
    {
        // 对话框默认定位到导出目录并预选最新文件，点一下即可导入。
        var file = _ui.PickImportFile(_service.ExportsDirectory, _service.ListExportFiles());
        if (file is null) return;

        if (!_ui.Confirm("导入账号",
            $"将从以下文件导入账号：\n\n{file}\n\n"
            + "已存在的邮箱会更新密码 / 2FA / 购买时间 / 备注（仅覆盖文件中有值的字段），"
            + "新邮箱会追加为账号。导入前会自动备份当前数据。\n\n继续？"))
        {
            return;
        }

        await RunGlobalAsync(async ct =>
        {
            var result = await _service.ImportAsync(file, ct);
            await ReloadFromServiceAsync(ct);
            var summary = result.Summary;
            var message = $"导入完成：新增 {summary.Added} 个，更新 {summary.Updated} 个"
                + (summary.Skipped > 0 ? $"，跳过 {summary.Skipped} 行" : string.Empty);
            StatusText = message + " · 备份：" + (result.BackupPath is null ? "无" : Path.GetFileName(result.BackupPath));
            _ui.Info("导入完成", message + (result.BackupPath is null ? string.Empty : $"\n\n导入前备份：\n{result.BackupPath}"));
        }, null);
    }

    private void OpenExportsFolder() => _ui.RevealInExplorer(_service.ExportsDirectory);

    private async Task ReloadFromServiceAsync(CancellationToken cancellationToken)
    {
        Accounts.Clear();
        foreach (var snapshot in await _service.LoadAccountsAsync(cancellationToken)) Accounts.Add(CreateAccount(snapshot));
        NormalizeValidityGroups();
        OnClockTick(this, EventArgs.Empty);
        NotifyCounts();
        QueryAllCommand.NotifyCanExecuteChanged();
    }

    private async Task EditAccountAsync(AccountViewModel account)
    {
        var request = AccountEditorWindow.ShowDialog(account.ToEditRequest());
        if (request is null) return;
        await account.ApplyEditAsync(request); AccountsView.Refresh(); NotifyCounts();
    }

    private async Task DeleteAccountAsync(AccountViewModel account)
    {
        if (!_ui.Confirm("删除账号", $"确定删除 {account.Email}？账号数据和本地授权令牌将不再从界面访问。")) return;
        await _service.DeleteAccountAsync(account.Id); Accounts.Remove(account); NotifyCounts();
        QueryAllCommand.NotifyCanExecuteChanged(); ShowToast("账号已删除", false);
    }

    private async Task QueryAllAsync()
    {
        IsBusy = true;
        try
        {
            var targets = Accounts.Where(x => !x.IsInvalid).ToArray(); var done = 0; var failed = 0;
            foreach (var account in targets)
            {
                if (!await account.QueryUsageFromBatchAsync()) failed++;
                done++; StatusText = $"正在查询用量 {done}/{targets.Length}";
            }
            StatusText = $"用量已刷新 · {targets.Length - failed} 成功" + (failed > 0 ? $" · {failed} 失败" : string.Empty);
            ShowToast(StatusText, failed > 0);
        }
        finally { IsBusy = false; }
    }

    private async Task RestartCodexAsync()
    {
        if (!_ui.Confirm("重启 Codex", "正在运行的 ChatGPT / Codex 任务会被中断，确定继续？")) return;
        await RunGlobalAsync(_service.RestartCodexAsync, "ChatGPT / Codex 已重新启动");
    }

    private void ToggleTheme()
    {
        ThemeService.Toggle();
        ThemeLabel = ThemeService.ThemeLabel;
    }

    private async Task ToggleKeepAliveAsync()
    {
        var intended = !IsKeepAliveEnabled;
        await RunGlobalAsync(async ct => { await _service.SetKeepAliveAsync(intended, ct); IsKeepAliveEnabled = intended; }, intended ? "保活已开启" : "保活已关闭");
    }

    private AccountViewModel CreateAccount(AccountSnapshot snapshot) =>
        new(snapshot, _service, _ui, EditAccountAsync, DeleteAccountAsync, OnAccountStateChanged, ShowToast);

    private async void OnAccountStateChanged(AccountViewModel changed)
    {
        if (changed.IsCurrentCodex)
            foreach (var account in Accounts.Where(x => !ReferenceEquals(x, changed))) account.IsCurrentCodex = false;

        var before = CaptureOrder();
        var current = Accounts.IndexOf(changed);
        if (current >= 0)
        {
            Accounts.RemoveAt(current);
            var destination = changed.IsInvalid ? Accounts.Count : Accounts.Count(x => !x.IsInvalid);
            Accounts.Insert(destination, changed);
        }
        AccountsView.Refresh();
        NotifyCounts();
        if (!before.SequenceEqual(Accounts)) await CommitOrderAsync(before);
    }

    private void NormalizeValidityGroups()
    {
        var ordered = AccountOrdering.StableValidityGroups(Accounts, x => x.IsInvalid);
        RestoreOrder(ordered);
    }
    private bool FilterAccount(object item) => item is not AccountViewModel a || string.IsNullOrWhiteSpace(SearchText)
        || a.Email.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
        || a.Note.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
        || a.PlanName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    private void OnClockTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now; var seconds = 30 - (int)(now.ToUnixTimeSeconds() % 30);
        GlobalTotpRemaining = seconds; GlobalTotpCountdown = $"{seconds:00}s";
        foreach (var account in Accounts) account.Tick(now);
    }

    private async Task RunGlobalAsync(Func<CancellationToken, Task> action, string? success)
    {
        if (IsBusy) return; IsBusy = true;
        try { await action(CancellationToken.None); if (success is not null) ShowToast(success, false); }
        catch (Exception ex) { ShowToast(ex.Message, true); }
        finally { IsBusy = false; }
    }

    private async Task HideToastAsync(CancellationToken token)
    {
        try { await Task.Delay(2600, token); IsToastVisible = false; } catch (OperationCanceledException) { }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(CanReorder)); OnPropertyChanged(nameof(VisibleAccountCount)); OnPropertyChanged(nameof(InvalidAccountCount));
    }

    public void Dispose() { _clock.Stop(); _toastCancellation?.Cancel(); _service.Dispose(); }
}
