using GptPlusManager.Core.Services;

namespace GptPlusManager.Core.Tests;

public sealed class AccountOrderingTests
{
    [Theory]
    [InlineData(0, 3, 0, 4, 2)] // first dragged after second
    [InlineData(2, 0, 0, 4, 0)] // third dragged to start
    [InlineData(1, 2, 0, 4, 1)] // same effective position
    [InlineData(2, 4, 0, 4, 3)] // dragged to group end
    public void ResolveMoveIndex_UsesInsertionBoundaries(int oldIndex, int boundary, int start, int end, int expected)
    {
        Assert.Equal(expected, AccountOrdering.ResolveMoveIndex(oldIndex, boundary, start, end));
    }

    [Fact]
    public void ResolveMoveIndex_ClampsWithinValidityGroup()
    {
        Assert.Equal(2, AccountOrdering.ResolveMoveIndex(3, 0, 2, 5));
        Assert.Equal(4, AccountOrdering.ResolveMoveIndex(3, 99, 2, 5));
    }

    [Fact]
    public void StableValidityGroups_PreservesOrderInsideGroups()
    {
        var input = new[] { ("A", false), ("B", true), ("C", false), ("D", true), ("E", false) };
        var output = AccountOrdering.StableValidityGroups(input, x => x.Item2);
        Assert.Equal(new[] { "A", "C", "E", "B", "D" }, output.Select(x => x.Item1));
    }
}
// ---- 默认排序：订阅优先 ----

public sealed class PlanOrderingTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("free", false)]
    [InlineData("Free plan", false)]
    [InlineData("plus", true)]
    [InlineData("Plus", true)]
    [InlineData("pro", true)]
    [InlineData("team", true)]
    public void IsSubscribedPlan_NonEmptyNonFree(string? plan, bool expected)
    {
        Assert.Equal(expected, AccountOrdering.IsSubscribedPlan(plan));
    }

    [Fact]
    public void PlanSortKey_SubscribedFirst_InvalidLast()
    {
        Assert.Equal(0, AccountOrdering.PlanSortKey(isInvalid: false, "plus"));
        Assert.Equal(1, AccountOrdering.PlanSortKey(isInvalid: false, "free"));
        Assert.Equal(1, AccountOrdering.PlanSortKey(isInvalid: false, null));
        // 无效永远沉底，即便它有订阅
        Assert.Equal(2, AccountOrdering.PlanSortKey(isInvalid: true, "plus"));
    }

    /// <summary>
    /// 默认排序的完整约定：订阅在前、其余在后、无效沉底；
    /// 同一档内保持用户手动排的顺序（稳定排序是"手动排序仍有意义"的前提）。
    /// </summary>
    [Fact]
    public void StableOrdered_SubscriptionFirst_PreservesManualOrderWithinTier()
    {
        var accounts = new[]
        {
            ("free-1", 1), ("plus-1", 0), ("invalid-1", 2),
            ("plus-2", 0), ("free-2", 1), ("plus-3", 0),
        };

        var ordered = AccountOrdering.StableOrdered(accounts, a => a.Item2);

        Assert.Equal(
            ["plus-1", "plus-2", "plus-3", "free-1", "free-2", "invalid-1"],
            ordered.Select(a => a.Item1).ToArray());
    }
}
