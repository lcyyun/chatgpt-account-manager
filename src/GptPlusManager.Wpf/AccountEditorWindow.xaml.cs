using System.IO;
using System.Windows;
using GptPlusManager.Core.Codex;
using GptPlusManager.Wpf.Infrastructure;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf;

/// <summary>
/// 单个账号编辑器。编辑模式只保存既有账号；添加模式在弹窗内分「手动添加 / 批量导入」两页。
///
/// <para>密码与 2FA 密钥默认打码（PasswordBox + 眼睛图标）。两对输入框互为镜像但<b>不做</b>
/// 持续同步：值只在切换可见性的一瞬间从可见框搬运到隐藏框，保存/清空也只认可见框——
/// 持续同步会让两个 Changed 事件互相触发（第三方页为此踩过坑），而这里根本不需要。</para>
/// </summary>
public partial class AccountEditorWindow : Window
{
    private readonly IAccountManagerService? _service;
    private readonly IUiService? _ui;
    private readonly bool _isAddMode;
    private bool _committed;
    private bool _passwordRevealed;
    private bool _secretRevealed;

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
            SetPassword(existing.Password);
            SetSecret(existing.TwoFactorSecret);
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
        // 只读可见框：打码时读 PasswordBox，显示时读明文框。隐藏框的值在切换瞬间
        // 已被搬运，任何时刻两框里至多一个是"当前值"，且它一定在可见框里。
        request = new AccountEditRequest(EmailBox.Text.Trim(), PasswordInput.Trim(), SecretInput.Trim(),
            PurchasedPicker.SelectedDate.HasValue ? new DateTimeOffset(PurchasedPicker.SelectedDate.Value) : null,
            NoteBox.Text);
        return true;
    }

    private void ClearForm()
    {
        EmailBox.Text = string.Empty;
        SetPassword(string.Empty);
        SetSecret(string.Empty);
        PurchasedPicker.SelectedDate = null;
        NoteBox.Text = string.Empty;
    }

    // ---------- 密码 / 2FA 密钥的打码输入 ----------

    private string PasswordInput => _passwordRevealed ? PasswordPlainBox.Text : PasswordBox.Password;
    private string SecretInput => _secretRevealed ? SecretPlainBox.Text : SecretBox.Password;

    private void SetPassword(string value)
    {
        // 只写值，不动可见性：预填与清空都发生在打码状态下，
        // 是否显示明文完全由眼睛图标决定。
        PasswordBox.Password = value;
        PasswordPlainBox.Text = value;
    }

    private void SetSecret(string value)
    {
        SecretBox.Password = value;
        SecretPlainBox.Text = value;
    }

    private void TogglePasswordReveal_Click(object sender, RoutedEventArgs e) =>
        ApplyPasswordReveal(!_passwordRevealed);

    private void ToggleSecretReveal_Click(object sender, RoutedEventArgs e) =>
        ApplySecretReveal(!_secretRevealed);

    private void ApplyPasswordReveal(bool revealed)
    {
        // 取值方向由 RevealToggle 决定（它被测试固定）：看切换前谁可见，谁才持有当前值。
        var value = RevealToggle.Resolve(_passwordRevealed, PasswordBox.Password, PasswordPlainBox.Text);
        _passwordRevealed = revealed;
        PasswordBox.Password = value;
        PasswordPlainBox.Text = value;
        PasswordBox.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        PasswordPlainBox.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        PasswordEyeIcon.Data = (System.Windows.Media.Geometry)FindResource(revealed ? "IconEyeOff" : "IconEye");
    }

    private void ApplySecretReveal(bool revealed)
    {
        var value = RevealToggle.Resolve(_secretRevealed, SecretBox.Password, SecretPlainBox.Text);
        _secretRevealed = revealed;
        SecretBox.Password = value;
        SecretPlainBox.Text = value;
        SecretBox.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        SecretPlainBox.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        SecretEyeIcon.Data = (System.Windows.Media.Geometry)FindResource(revealed ? "IconEyeOff" : "IconEye");
    }

    private void SetStatus(string message, bool isError)
    {
        AddStatus.Text = message;
        // 用 SetResourceReference 而非 FindResource，切换日夜主题时颜色仍会跟随。
        AddStatus.SetResourceReference(ForegroundProperty, isError ? "DangerBrush" : "GreenBrush");
        AddStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
