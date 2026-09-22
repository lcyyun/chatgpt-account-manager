namespace GptPlusManager.Core.Codex;

/// <summary>
/// 密钥明/隐切换时的取值规则。
///
/// <para>看着简单，但很容易写错：界面上是"掩码框 + 明文框"两个控件互为镜像，
/// 切换时若把取值方向按<b>目标状态</b>来定，就会去读那个当前为空的框，
/// 再写回两个框——结果是密钥被拼接成两份重复。这个 bug 真实发生过。</para>
///
/// <para>规则只有一条：<b>取切换前可见的那个框的值</b>。放在这里是为了能被测试固定住，
/// 而不是埋在 code-behind 里靠人工点界面验证。</para>
/// </summary>
public static class RevealToggle
{
    /// <summary>
    /// 计算切换后两个框应有的共同值。
    /// </summary>
    /// <param name="currentlyRevealed">切换<b>之前</b>是否处于明文可见状态。</param>
    /// <param name="maskedValue">掩码框当前的值。</param>
    /// <param name="plainValue">明文框当前的值。</param>
    public static string Resolve(bool currentlyRevealed, string? maskedValue, string? plainValue) =>
        currentlyRevealed ? plainValue ?? string.Empty : maskedValue ?? string.Empty;
}
