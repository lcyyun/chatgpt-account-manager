using System.Windows;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf;

/// <summary>编辑单个已有账号。批量导入由「添加账号」页负责。</summary>
public partial class AccountEditorWindow : Window
{
    private AccountEditorWindow(AccountEditRequest existing)
    {
        InitializeComponent();
        EmailBox.Text = existing.Email;
        PasswordBox.Text = existing.Password;
        SecretBox.Text = existing.TwoFactorSecret;
        PurchasedPicker.SelectedDate = existing.PurchasedAt?.LocalDateTime;
        NoteBox.Text = existing.Note;
    }

    public AccountEditRequest? Result { get; private set; }

    /// <summary>返回 null 表示用户取消。</summary>
    public static AccountEditRequest? ShowDialog(AccountEditRequest existing)
    {
        var dialog = new AccountEditorWindow(existing) { Owner = Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EmailBox.Text))
        {
            MessageBox.Show("邮箱不能为空。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            EmailBox.Focus();
            return;
        }
        Result = new AccountEditRequest(EmailBox.Text.Trim(), PasswordBox.Text, SecretBox.Text.Trim(),
            PurchasedPicker.SelectedDate.HasValue ? new DateTimeOffset(PurchasedPicker.SelectedDate.Value) : null,
            NoteBox.Text);
        DialogResult = true;
    }
}
