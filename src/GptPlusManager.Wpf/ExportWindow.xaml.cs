using System.Collections.Generic;
using System.IO;
using System.Windows;
using GptPlusManager.Wpf.Infrastructure;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf;

/// <summary>把"导出文本"和"JSON 备份"合并到一个窗口，并明确展示导出目录。</summary>
public partial class ExportWindow : Window
{
    private readonly IAccountManagerService _service;
    private readonly IUiService _ui;

    private ExportWindow(IAccountManagerService service, IUiService ui)
    {
        InitializeComponent();
        _service = service;
        _ui = ui;
        DirectoryText.Text = service.ExportsDirectory;
    }

    public static void ShowDialog(IAccountManagerService service, IUiService ui)
    {
        var dialog = new ExportWindow(service, ui) { Owner = Application.Current?.MainWindow };
        dialog.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _ui.RevealInExplorer(_service.ExportsDirectory);

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (JsonBackupCheck.IsChecked != true && TextExportCheck.IsChecked != true)
        {
            ExportStatus.Text = "请至少选择一种导出格式。";
            return;
        }

        var written = new List<string>();
        try
        {
            if (JsonBackupCheck.IsChecked == true) written.Add(await _service.BackupJsonAsync());
            if (TextExportCheck.IsChecked == true) written.Add(await _service.ExportTextAsync());

            ExportStatus.Text = "导出完成：\n" + string.Join("\n", written.ConvertAll(Path.GetFileName));
            // 定位到最后一个文件，方便立即查看或复制。
            _ui.RevealInExplorer(written[^1]);
        }
        catch (Exception ex)
        {
            ExportStatus.Text = "导出失败：" + ex.Message;
        }
    }
}
