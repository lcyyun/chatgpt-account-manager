using System.Diagnostics;
using GptPlusManager.Core.Models;

namespace GptPlusManager.Core.Services;

public sealed class ClientProcessService
{
    private static readonly string[] ProcessNames = ["ChatGPT", "codex", "codex-code-mode-host"];
    private const string ApplicationId = "shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App";

    public async Task<OperationResult> RestartAsync(
        TimeSpan? gracefulTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Failure(
                "Restarting the Codex Windows application is only supported on Windows.",
                "platform_not_supported");
        }

        try
        {
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            _ = process.CloseMainWindow();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }

            var deadline = DateTimeOffset.UtcNow + (gracefulTimeout ?? TimeSpan.FromSeconds(5));
            while (DateTimeOffset.UtcNow < deadline && Process.GetProcessesByName("ChatGPT").Length > 0)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            foreach (var processName in ProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            process.Kill(true);
                            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            Process.Start(new ProcessStartInfo("explorer.exe", ApplicationId) { UseShellExecute = true });
            return OperationResult.Success("ChatGPT / Codex restarted.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OperationResult.Failure(exception.Message, "restart_failed");
        }
    }
}
