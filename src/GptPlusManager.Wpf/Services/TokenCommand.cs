using System.IO;
using System.Text;
using GptPlusManager.Core.Codex;

namespace GptPlusManager.Wpf.Services;

/// <summary>
/// 实现 <c>--provider-token &lt;id&gt;</c>：把指定供应商的密钥写到标准输出后退出。
///
/// <para>Codex 的 <c>auth = { command = ... }</c> 机制会执行这个命令并拿它的 stdout
/// 当 bearer token，从而让密钥不必出现在 <c>config.toml</c> 里。</para>
///
/// <para><b>必须直写标准输出句柄</b>：本程序是 WinExe（GUI 子系统），没有控制台。
/// 只有当父进程重定向了 stdout 时句柄才有效——Codex 正是这么做的。用
/// <see cref="Console.OpenStandardOutput"/> 直写字节，比 <c>Console.WriteLine</c>
/// 更可靠，也避免任何编码/缓冲的意外。</para>
///
/// <para>这个路径绝不能弹窗：它由 Codex 在后台调用，任何 UI 都会让 Codex 挂住。</para>
/// </summary>
internal static class TokenCommand
{
    public static int Run(string providerId)
    {
        try
        {
            var secrets = new ProviderSecrets();
            var key = secrets.GetAsync(providerId).GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(key))
            {
                WriteError($"未找到供应商 \"{providerId}\" 的 API Key。");
                return 3;
            }

            // 只输出 token 本身，不要换行之外的任何装饰——Codex 会原样当作 bearer token。
            using var stdout = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(key.Trim());
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return 0;
        }
        catch (Exception exception)
        {
            WriteError(exception.Message);
            return 1;
        }
    }

    private static void WriteError(string message)
    {
        try
        {
            using var stderr = Console.OpenStandardError();
            var bytes = Encoding.UTF8.GetBytes("gptplus: " + message + Environment.NewLine);
            stderr.Write(bytes, 0, bytes.Length);
            stderr.Flush();
        }
        catch (IOException)
        {
            // 连 stderr 都不可用就只能靠退出码了。
        }
    }
}
