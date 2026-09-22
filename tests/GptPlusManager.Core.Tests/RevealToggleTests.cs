using GptPlusManager.Core.Codex;

namespace GptPlusManager.Core.Tests;

/// <summary>
/// 锁定密钥明/隐切换的取值规则。
///
/// <para>回归来源：曾把取值方向按目标状态来定，于是切换时读了当前为空的框，
/// 再把值写回两个框，密钥被拼接成两份重复（sk-xxxsk-xxx）。用真实界面才发现，
/// 这里用测试固定住。</para>
/// </summary>
public sealed class RevealToggleTests
{
    private const string Key = "sk-reveal-test-12345";

    [Fact]
    public void Reveal_TakesValueFromTheMaskedBox()
    {
        // 切换前处于隐藏态：值在掩码框里，明文框可能为空或过期。
        var value = RevealToggle.Resolve(currentlyRevealed: false, maskedValue: Key, plainValue: "");

        Assert.Equal(Key, value);
    }

    [Fact]
    public void Hide_TakesValueFromThePlainBox()
    {
        // 切换前处于明文态：用户在明文框里编辑过，掩码框可能是旧的。
        var value = RevealToggle.Resolve(currentlyRevealed: true, maskedValue: "", plainValue: Key);

        Assert.Equal(Key, value);
    }

    [Fact]
    public void RoundTrip_DoesNotDuplicateTheValue()
    {
        // 这正是真实踩到的坑：反复切换后值变成两份拼接。
        var masked = Key;
        var plain = "";

        for (var i = 0; i < 6; i++)
        {
            var revealed = i % 2 == 0;              // 目标状态与当前相反
            var current = !revealed;
            var value = RevealToggle.Resolve(current, masked, plain);
            masked = value;
            plain = value;

            Assert.Equal(Key, value);
            Assert.Equal(masked, plain);
        }
    }

    [Fact]
    public void NullValues_ResolveToEmptyStringNotNull()
    {
        // 控件在某些时机返回 null；直接赋给另一个控件会抛。
        Assert.Equal(string.Empty, RevealToggle.Resolve(false, null, null));
        Assert.Equal(string.Empty, RevealToggle.Resolve(true, null, null));
    }

    [Fact]
    public void EmptyKey_StaysEmpty()
    {
        Assert.Equal(string.Empty, RevealToggle.Resolve(false, "", ""));
    }

    [Fact]
    public void UserEditInPlainBox_WinsOnHide()
    {
        // 用户在明文框里改了内容后点隐藏：新值必须保留，不能被掩码框的旧值覆盖。
        var value = RevealToggle.Resolve(currentlyRevealed: true, maskedValue: "old-key", plainValue: "new-key");

        Assert.Equal("new-key", value);
    }
}
