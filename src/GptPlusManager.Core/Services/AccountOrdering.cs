namespace GptPlusManager.Core.Services;

public static class AccountOrdering
{
    public static int ResolveMoveIndex(int oldIndex, int insertionBoundary, int groupStart, int groupEndExclusive)
    {
        if (oldIndex < groupStart || oldIndex >= groupEndExclusive)
            throw new ArgumentOutOfRangeException(nameof(oldIndex));
        insertionBoundary = Math.Clamp(insertionBoundary, groupStart, groupEndExclusive);
        var newIndex = insertionBoundary > oldIndex ? insertionBoundary - 1 : insertionBoundary;
        return Math.Clamp(newIndex, groupStart, Math.Max(groupStart, groupEndExclusive - 1));
    }

    public static IReadOnlyList<T> StableValidityGroups<T>(IEnumerable<T> source, Func<T, bool> isInvalid)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(isInvalid);
        var items = source.ToList();
        return items.Where(x => !isInvalid(x)).Concat(items.Where(isInvalid)).ToArray();
    }
}
