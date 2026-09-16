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
