using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Text;

namespace GlamourLog.Features.Cabinet;

internal sealed partial class CabinetListHandler {
    private const int MaxHiddenItemLogLines = 48;

    private string? _lastFilterSummary;
    private string? _lastPreFilter;
    private string? _lastPostFilter;
    private string? _lastHiddenItems;
    private string? _lastApplyPipeline;
    private string? _lastFilterOffState;
    private string? _lastFilterOnState;
    private string? _lastSnapshotUnavailableSignature;

    private void ClearFilterLogSignatures() {
        _lastFilterSummary = null;
        _lastPreFilter = null;
        _lastPostFilter = null;
        _lastHiddenItems = null;
        _lastApplyPipeline = null;
        _lastFilterOffState = null;
        _lastFilterOnState = null;
        _lastSnapshotUnavailableSignature = null;
    }

    private static void LogFilterDebug(string phase, string message)
        => Svc.Log.Information($"[{nameof(CabinetListHandler)}.{phase}] {message}");

    private unsafe void LogSnapshotUnavailableOnce(AddonCabinet* addon, AgentCabinet* agent) {
        var list = addon->ItemList;
        var listCount = list is not null ? list->GetItemCount() : -1;
        var count = ReadCategoryItemCount(addon, agent);
        var signature =
            $"cat={addon->CategoryIndex} agentCat={agent->SelectedCategoryIndex} count={count} " +
            $"list={listCount} pendingUpdate={agent->PendingUpdate} snapshot={_rows.Length}";
        if (signature == _lastSnapshotUnavailableSignature)
            return;
        _lastSnapshotUnavailableSignature = signature;
        LogFilterDebug(nameof(OnPostRefresh), $"waiting for native category data ({signature})");
    }

    private unsafe void LogApplyPipelineResult(AddonCabinet* addon, int visible, int snapshot) {
        var list = addon->ItemList;
        var listCount = list is not null ? list->GetItemCount() : -1;
        var applySummary = $"visible={visible} snapshot={snapshot} category={addon->CategoryIndex} getItemCount={listCount}";
        if (applySummary == _lastApplyPipeline)
            return;
        _lastApplyPipeline = applySummary;
        LogFilterDebug(nameof(Project), $"applied {applySummary}");
    }

    private unsafe void LogFilterOnState(string phase, AddonCabinet* addon, AgentCabinet* agent, int snapshot, int visible) {
        var list = addon->ItemList;
        var listCount = list is not null ? list->GetItemCount() : -1;
        var summary =
            $"visible={visible} snapshot={snapshot} category={addon->CategoryIndex} agentItemCount={agent->ItemCount} " +
            $"nativeGetItemCount={listCount}";
        if (summary == _lastFilterOnState)
            return;
        _lastFilterOnState = summary;
        LogFilterDebug(phase, summary);
    }

    private unsafe void LogFilterOffState(string phase, AddonCabinet* addon, AgentCabinet* agent) {
        var list = addon->ItemList;
        var listCount = list is not null ? list->GetItemCount() : -1;
        var summary =
            $"category={addon->CategoryIndex} count={ReadCategoryItemCount(addon, agent)} agentItemCount={agent->ItemCount} " +
            $"nativeGetItemCount={listCount}";
        if (summary == _lastFilterOffState)
            return;
        _lastFilterOffState = summary;
        LogFilterDebug(phase, summary);
    }

    private void LogFilterRebuildSummary(uint category, int snapshot, int visible, uint[] itemIds, List<int> visibleIndices) {
        var hiddenCount = snapshot - visible;
        var signature = $"category={category} snapshot={snapshot} visible={visible} hidden={hiddenCount} filters=[{DescribeEnabledFilters()}]";
        if (signature != _lastFilterSummary) {
            _lastFilterSummary = signature;
            LogFilterDebug("RebuildFilterMap", signature);
            if (hiddenCount > 0)
                LogHiddenItemDecisions(itemIds);
        }

        var pre = FormatIndexedItemSet(Enumerable.Range(0, itemIds.Length), itemIds);
        if (pre != _lastPreFilter) {
            _lastPreFilter = pre;
            LogFilterDebug("pre-filter", pre);
        }

        var post = FormatIndexedItemSet(visibleIndices, itemIds);
        if (post != _lastPostFilter) {
            _lastPostFilter = post;
            LogFilterDebug("post-filter", post);
        }
    }

    private void LogHiddenItemDecisions(uint[] itemIds) {
        var lines = new List<string>(itemIds.Length);
        for (var i = 0; i < itemIds.Length; i++) {
            if (!ShouldExcludeItem(itemIds[i]))
                continue;
            lines.Add($"{FormatFilterRow(i, itemIds[i])} => {DescribeHideReasons(itemIds[i])}");
        }

        var signature = string.Join('\n', lines);
        if (signature == _lastHiddenItems)
            return;
        _lastHiddenItems = signature;

        var logCount = Math.Min(lines.Count, MaxHiddenItemLogLines);
        for (var i = 0; i < logCount; i++)
            LogFilterDebug("hidden", lines[i]);
        if (lines.Count > logCount)
            LogFilterDebug("hidden", $"... and {lines.Count - logCount} more hidden row(s)");
    }

    private string DescribeEnabledFilters() {
        var enabled = _filters.Where(f => f.IsEnabled).Select(FilterDebugLabel).ToArray();
        return enabled.Length == 0 ? "none" : string.Join(", ", enabled);
    }

    private static string FilterDebugLabel(ICabinetRowFilter filter) => filter switch {
        HideDepositedItemsFilter => "owned",
        HideGearsetItemsFilter => "gearset",
        _ => filter.GetType().Name,
    };

    private string DescribeHideReasons(uint itemId) {
        if (itemId == 0)
            return "empty slot";
        var reasons = new List<string>();
        foreach (var filter in _filters) {
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
}
