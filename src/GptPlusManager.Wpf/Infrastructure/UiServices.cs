using System.Windows;

namespace GptPlusManager.Wpf.Infrastructure;

public interface IUiService
{
    void CopyText(string text);
    bool Confirm(string title, string message);
}

public sealed class WpfUiService : IUiService
{
    public void CopyText(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
