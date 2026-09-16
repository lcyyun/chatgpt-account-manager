using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace GptPlusManager.Wpf.Infrastructure;

public interface IUiService
{
    void CopyText(string text);
    bool Confirm(string title, string message);
    void Info(string title, string message);

    /// <summary>选择要导入的文件。对话框默认打开 <paramref name="initialDirectory"/> 并预选最新文件。</summary>
    string? PickImportFile(string initialDirectory, IReadOnlyList<string> suggestedFiles);

    /// <summary>在资源管理器中打开目录并选中指定文件（文件不存在时只打开目录）。</summary>
    void RevealInExplorer(string path);
}

public sealed class WpfUiService : IUiService
{
    public void CopyText(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Clipboard.SetText(text);
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void Info(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? PickImportFile(string initialDirectory, IReadOnlyList<string> suggestedFiles)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的账号文件",
            Filter = "账号文件 (*.json;*.txt)|*.json;*.txt|JSON 备份 (*.json)|*.json|文本清单 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
            // 预选最新导出文件，用户直接点"打开"即可，无需在列表中查找。
            var newest = suggestedFiles.FirstOrDefault(File.Exists);
            if (newest is not null) dialog.FileName = Path.GetFileName(newest);
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // 打开资源管理器失败不应影响主流程。
        }
    }
}
