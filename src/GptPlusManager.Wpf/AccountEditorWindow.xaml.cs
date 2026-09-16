using System.IO;
using System.Windows;
using GptPlusManager.Wpf.Infrastructure;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf;

/// <summary>
/// 单个账号编辑器。编辑模式只保存既有账号；添加模式在弹窗内分「手动添加 / 批量导入」两页。
/// </summary>
public partial class AccountEditorWindow : Window
{
    private readonly IAccountManagerService? _service;
    private readonly IUiService? _ui;
    private readonly bool _isAddMode;
    private bool _committed;

    private AccountEditorWindow(AccountEditRequest? existing, IAccountManagerService? service, IUiService? ui)
    {
        InitializeComponent();
        _service = service;
        _ui = ui;
        _isAddMode = existing is null;

        if (_isAddMode)
        {
            Title = "添加账号";
            HeaderText.Text = "添加账号";
            Width = 660;
            TabsRow.Visibility = Visibility.Visible;
            ManualButtons.Visibility = Visibility.Visible;
            DirectoryText.Text = service!.ExportsDirectory;
        }
        else
        {
            EditButtons.Visibility = Visibility.Visible;
            EmailBox.Text = existing!.Email;
            PasswordBox.Text = existing.Password;
            SecretBox.Text = existing.TwoFactorSecret;
            PurchasedPicker.SelectedDate = existing.PurchasedAt?.LocalDateTime;
            NoteBox.Text = existing.Note;
        }
    }

    public AccountEditRequest? Result { get; private set; }

    /// <summary>编辑既有账号；返回 null 表示用户取消。</summary>
    public static AccountEditRequest? ShowDialog(AccountEditRequest existing)
    {
        var dialog = new AccountEditorWindow(existing, null, null) { Owner = Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>添加账号弹窗；返回是否至少写入过一次，调用方据此决定是否重载列表。</summary>
    public static bool ShowAddDialog(IAccountManagerService service, IUiService ui)
    {
        var dialog = new AccountEditorWindow(null, service, ui) { Owner = Application.Current?.MainWindow };
        dialog.ShowDialog();
        return dialog._committed;
    }

    private void AddTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isAddMode) return;
        var showImport = ImportTab.IsChecked == true;
        ImportPage.Visibility = showImport ? Visibility.Visible : Visibility.Collapsed;
        FormPage.Visibility = showImport ? Visibility.Collapsed : Visibility.Visible;
        ImportButtons.Visibility = showImport ? Visibility.Visible : Visibility.Collapsed;
        ManualButtons.Visibility = showImport ? Visibility.Collapsed : Visibility.Visible;
        // 隐藏那一页不应再响应回车默认动作。
        SaveAndAddButton.IsDefault = !showImport;
        if (!showImport) EmailBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var request)) return;
        Result = request;
        DialogResult = true;
    }

    private async void SaveAndAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var request)) return;

        ManualButtons.IsEnabled = false;
        try
        {
            var created = await _service!.AddAccountAsync(request);
            _committed = true;
            ClearForm();
            SetStatus($"已添加 {created.Email}，可继续录入下一个。", false);
        }
        catch (Exception ex)
        {
            SetStatus("添加失败：" + ex.Message, true);
        }
        finally { ManualButtons.IsEnabled = true; }
    }

    private void ClearForm_Click(object sender, RoutedEventArgs e)
    {
        ClearForm();
        SetStatus(string.Empty, false);
        EmailBox.Focus();
    }

    private void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var file = _ui!.PickImportFile(_service!.ExportsDirectory, _service.ListExportFiles());
        if (file is null) return;
        try
        {
            ImportTextBox.Text = File.ReadAllText(file);
            SetStatus($"已载入 {Path.GetFileName(file)}，点「开始导入」执行合并。", false);
        }
        catch (Exception ex)
        {
            SetStatus("读取文件失败：" + ex.Message, true);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ImportTextBox.Text))
        {
            SetStatus("请先粘贴内容或从文件选择。", true);
            return;
        }

        ImportButtons.IsEnabled = false;
        try
        {
            var result = await _service!.ImportTextAsync(ImportTextBox.Text, "添加账号导入");
            var summary = result.Summary;
            var text = $"导入完成：新增 {summary.Added} 个，更新 {summary.Updated} 个"
                + (summary.Skipped > 0 ? $"，跳过 {summary.Skipped} 行" : string.Empty)
                + (result.BackupPath is null ? string.Empty : $"（导入前已备份 {Path.GetFileName(result.BackupPath)}）");
            _committed = true;
            ImportTextBox.Text = string.Empty;
            SetStatus(text, false);
        }
        catch (Exception ex)
        {
            SetStatus("导入失败：" + ex.Message, true);
        }
        finally { ImportButtons.IsEnabled = true; }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _ui!.RevealInExplorer(_service!.ExportsDirectory);

    private bool TryReadForm(out AccountEditRequest request)
    {
        request = default!;
        if (string.IsNullOrWhiteSpace(EmailBox.Text))
        {
            SetStatus("邮箱不能为空。", true);
            EmailBox.Focus();
            return false;
        }
        request = new AccountEditRequest(EmailBox.Text.Trim(), PasswordBox.Text, SecretBox.Text.Trim(),
            PurchasedPicker.SelectedDate.HasValue ? new DateTimeOffset(PurchasedPicker.SelectedDate.Value) : null,
            NoteBox.Text);
        return true;
    }

    private void ClearForm()
    {
        EmailBox.Text = string.Empty;
        PasswordBox.Text = string.Empty;
        SecretBox.Text = string.Empty;
        PurchasedPicker.SelectedDate = null;
        NoteBox.Text = string.Empty;
    }

    private void SetStatus(string message, bool isError)
    {
        AddStatus.Text = message;
        // 用 SetResourceReference 而非 FindResource，切换日夜主题时颜色仍会跟随。
        AddStatus.SetResourceReference(ForegroundProperty, isError ? "DangerBrush" : "GreenBrush");
        AddStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
