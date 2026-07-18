namespace GlamourLog.Features.Cabinet;

internal sealed partial class CabinetListHandler {
    private const int MaxHiddenItemLogLines = 48;

    private void LogFilterApplied(uint category, int source, int visible, uint[] itemIds, List<int> visibleIndices) {
        var hiddenCount = source - visible;
        LogFilterDebug(
            nameof(Project),
            $"category={category} source={source} visible={visible} hidden={hiddenCount} filters=[{DescribeEnabledFilters()}]");

        if (hiddenCount > 0)
            LogHiddenItemDecisions(itemIds);

        LogFilterDebug("pre-filter", FormatIndexedItemSet(Enumerable.Range(0, itemIds.Length), itemIds));
        LogFilterDebug("post-filter", FormatIndexedItemSet(visibleIndices, itemIds));
    }

    private void LogHiddenItemDecisions(uint[] itemIds) {
        var logged = 0;
        for (var i = 0; i < itemIds.Length; i++) {
            if (!ShouldExcludeItem(itemIds[i]))
                continue;
            if (logged >= MaxHiddenItemLogLines) {
                LogFilterDebug("hidden", $"... and more hidden row(s)");
                return;
            }

            LogFilterDebug("hidden", $"{FormatFilterRow(i, itemIds[i])} => {DescribeHideReasons(itemIds[i])}");
            logged++;
        }
    }

    private string DescribeHideReasons(uint itemId) {
        if (itemId == 0)
            return "empty slot";
        var reasons = new List<string>();
        foreach (var filter in Filters) {
            if (!filter.IsEnabled || !filter.ShouldHide(itemId))
                continue;
            reasons.Add(FilterDebugLabel(filter));
        }
        if (reasons.Count == 0)
            reasons.Add("no matching filter (unexpected)");
        return string.Join(", ", reasons);
    }

    private static string FormatIndexedItemSet(IEnumerable<int> sourceIndices, uint[] itemIds) {
        var parts = new List<string>();
        foreach (var index in sourceIndices) {
            if ((uint)index >= (uint)itemIds.Length)
                continue;
            parts.Add(FormatFilterRow(index, itemIds[index]));
        }
        return parts.Count == 0 ? "(none)" : string.Join("; ", parts);
    }

    private static string FormatFilterRow(int index, uint itemId) => $"[{index}] {(ItemHandle)itemId}";

    protected override string FilterDebugLabel(IRowFilter filter) => filter switch {
        HideDepositedItemsFilter => "owned",
        HideGearsetItemsFilter => "gearset",
        _ => filter.GetType().Name,
    };
}
