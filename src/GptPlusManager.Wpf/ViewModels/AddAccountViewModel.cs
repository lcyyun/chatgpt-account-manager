using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GptPlusManager.Wpf.Infrastructure;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf.ViewModels;

/// <summary>
/// "添加账号"页：同一页内既可手动录入单个账号，也可批量导入（粘贴或选文件）。
/// </summary>
public sealed partial class AddAccountViewModel : ObservableObject
{
    private readonly IAccountManagerService _service;
    private readonly IUiService _ui;
    private readonly Func<Task> _onCommitted;

    public AddAccountViewModel(IAccountManagerService service, IUiService ui, Func<Task> onCommitted)
    {
        _service = service;
        _ui = ui;
        _onCommitted = onCommitted;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        PickFileCommand = new RelayCommand(PickFile, () => !IsBusy);
        OpenExportsFolderCommand = new RelayCommand(() => _ui.RevealInExplorer(_service.ExportsDirectory));
        ClearFormCommand = new RelayCommand(ResetForm);
    }

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _twoFactorSecret = string.Empty;
    [ObservableProperty] private DateTimeOffset? _purchasedAt;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _importText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private bool _isBusy;

    public string ExportsDirectory => _service.ExportsDirectory;

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IRelayCommand PickFileCommand { get; }
    public IRelayCommand OpenExportsFolderCommand { get; }
    public IRelayCommand ClearFormCommand { get; }

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        PickFileCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            SetStatus("请填写邮箱。", true);
            return;
        }

        IsBusy = true;
        try
        {
            var request = new AccountEditRequest(Email.Trim(), Password, TwoFactorSecret.Trim(), PurchasedAt, Note);
            var created = await _service.AddAccountAsync(request);
            SetStatus($"已添加 {created.Email}", false);
            ResetForm();
            await _onCommitted();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
        finally { IsBusy = false; }
    }

    private void PickFile()
    {
        var file = _ui.PickImportFile(_service.ExportsDirectory, _service.ListExportFiles());
        if (file is null) return;
        try
        {
            ImportText = File.ReadAllText(file);
            SetStatus($"已载入 {Path.GetFileName(file)}，点「开始导入」执行合并。", false);
        }
        catch (Exception ex)
        {
            SetStatus("读取文件失败：" + ex.Message, true);
        }
    }

    private async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportText))
        {
            SetStatus("请先粘贴内容或从文件选择。", true);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _service.ImportTextAsync(ImportText, "添加页导入");
            var summary = result.Summary;
            var text = $"导入完成：新增 {summary.Added} 个，更新 {summary.Updated} 个"
                + (summary.Skipped > 0 ? $"，跳过 {summary.Skipped} 行" : string.Empty)
                + (result.BackupPath is null ? string.Empty : $"（导入前已备份 {Path.GetFileName(result.BackupPath)}）");
            SetStatus(text, false);
            ImportText = string.Empty;
            await _onCommitted();
        }
        catch (Exception ex)
        {
            SetStatus("导入失败：" + ex.Message, true);
        }
        finally { IsBusy = false; }
    }

    private void ResetForm()
    {
        Email = string.Empty;
        Password = string.Empty;
        TwoFactorSecret = string.Empty;
        PurchasedAt = null;
        Note = string.Empty;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
