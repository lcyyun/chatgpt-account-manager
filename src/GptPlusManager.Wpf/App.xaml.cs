using System.Diagnostics;
using System.Threading;
using System.Windows;
using GptPlusManager.Wpf.Services;

namespace GptPlusManager.Wpf;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 被 Codex 以 `--provider-token <id>` 调用：把该供应商的密钥打到 stdout 后退出。
        // 这是"命令式"密钥注入的实现——密钥因此不必写进 config.toml。
        // 必须在单实例检查之前：主程序开着时也要能被并发调用。
        if (e.Args.Length >= 2 && e.Args[0] == "--provider-token")
        {
            Shutdown(TokenCommand.Run(e.Args[1]));
            return;
        }

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
