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

    /// <summary>
    /// 是否处于订阅中的套餐。套餐名来自用量接口（plus / pro / team / free…），
    /// 判定标准是"非空且不是 free"——过期账号会被接口报成 free 或空，
    /// 不需要在这里再比对订阅到期时间。
    /// </summary>
    public static bool IsSubscribedPlan(string? planName) =>
        !string.IsNullOrWhiteSpace(planName) &&
        !planName.Contains("free", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 默认排序键：订阅中 0，未订阅 / 套餐未知 1，无效 2。
    /// </summary>
    public static int PlanSortKey(bool isInvalid, string? planName) =>
        isInvalid ? 2 : IsSubscribedPlan(planName) ? 0 : 1;

    /// <summary>
    /// 按键稳定排序（同键保持原有相对顺序）。LINQ 的 OrderBy 本身就是稳定排序，
    /// 这里包一层是为了把"默认排序 = PlanSortKey + 手动顺序作平手决胜"这个约定
    /// 固定在可测试的位置，而不是散落在界面代码里。
    /// </summary>
    public static IReadOnlyList<T> StableOrdered<T>(IEnumerable<T> source, Func<T, int> keySelector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);
        return source.OrderBy(keySelector).ToArray();
    }
}
