using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace GptPlusManager.Wpf;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "Local\\GptPlusManager.Wpf.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("GPTPlus 账号管理已经在运行。", "GPTPlus 账号管理", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // The legacy WinForms process does not participate in the new cross-process lock.
        // Refuse concurrent writes rather than risking duplicate refresh-token rotation.
        var legacyRunning = Process.GetProcessesByName("GptPlusManager")
            .Any(p => p.Id != Environment.ProcessId);
        if (legacyRunning)
        {
            MessageBox.Show("检测到旧版账号管理器正在运行。请先关闭旧版，再启动新版，避免两个程序同时写入账号和令牌数据。",
                "GPTPlus 账号管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        ThemeService.Initialize(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "gptplus"));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
