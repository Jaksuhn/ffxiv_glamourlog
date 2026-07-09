using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
        var contiguous = ScanContiguousPopulatedCategoryItemCount(agent);
        var inferred = _categoryItemCount > 0 ? _categoryItemCount : InferCategoryItemCount(addon, agent);
        var signature =
            $"cat={addon->CategoryIndex} inferred={inferred} list={listCount} contiguous={contiguous} " +
            $"pendingUpdate={agent->PendingUpdate} pass={_snapshotCapturePasses}";
        if (signature == _lastSnapshotUnavailableSignature)
            return;
        _lastSnapshotUnavailableSignature = signature;
        LogFilterDebug(nameof(OnPostRefresh), $"category snapshot unavailable after native refresh ({signature})");
    }

    private unsafe void LogApplyPipelineResult(AddonCabinet* addon) {
        var list = addon->ItemList;
        var listLength = list is not null ? list->ListLength : -1;
        var getItemCount = list is not null ? list->GetItemCount() : -1;
        var applySummary =
            $"visible={_displayToSource.Length} snapshot={_categoryRows.Length} category={_categoryIndex} " +
            $"categoryItemCount={_categoryItemCount} listLength={listLength} getItemCount={getItemCount} " +
            $"scrollRefreshPending={list is not null && list->IsScrollRefreshPending} updatePending={list is not null && list->IsUpdatePending}";
        if (applySummary == _lastApplyPipeline)
            return;
        _lastApplyPipeline = applySummary;
        LogFilterDebug(nameof(TryApplyFilterPipeline), $"applied {applySummary}");
    }

    private unsafe void LogFilterOnState(string phase, AddonCabinet* addon, AgentCabinet* agent) {
        var list = addon->ItemList;
        var nativeVisible = list is not null && ((AtkResNode*)list)->IsVisible();
        var listLength = list is not null ? list->ListLength : -1;
        var getItemCount = list is not null ? list->GetItemCount() : -1;
        var numVisible = list is not null ? list->NumVisibleItems : (short)-1;
        var itemHeight = list is not null ? list->ItemHeight : (short)-1;
        var listHeight = list is not null ? list->ListHeight : (short)-1;
        var hasRenderer = list is not null && list->FirstAtkComponentListItemRenderer is not null;
        var hasScrollBar = list is not null && list->ScrollBarComponent is not null;
        var summary =
            $"visible={_displayToSource.Length} snapshot={_categoryRows.Length} category={_categoryIndex} agentItemCount={agent->ItemCount} " +
            $"nativeVisible={nativeVisible} nativeListLength={listLength} nativeGetItemCount={getItemCount} nativeNumVisible={numVisible} " +
            $"itemHeight={itemHeight} listHeight={listHeight} hasRenderer={hasRenderer} hasScrollBar={hasScrollBar}";
        if (summary == _lastFilterOnState)
            return;
        _lastFilterOnState = summary;
        LogFilterDebug(phase, summary);
    }

    private unsafe void LogFilterOffState(string phase, AddonCabinet* addon, AgentCabinet* agent) {
        var list = addon->ItemList;
        var nativeVisible = list is not null && ((AtkResNode*)list)->IsVisible();
        var nativeListLength = list is not null ? list->ListLength : -1;
        var nativeGetItemCount = list is not null ? list->GetItemCount() : -1;
        var inferred = InferCategoryItemCount(addon, agent);
        var summary =
            $"snapshot={_categoryRows.Length} category={addon->CategoryIndex} inferred={inferred} agentItemCount={agent->ItemCount} " +
            $"nativeVisible={nativeVisible} nativeListLength={nativeListLength} nativeGetItemCount={nativeGetItemCount}";
        if (summary == _lastFilterOffState)
            return;
        _lastFilterOffState = summary;
        LogFilterDebug(phase, summary);
    }

    private void LogFilterRebuildSummary() {
        var hiddenCount = _categoryItemCount - _displayToSource.Length;
        var summary = new StringBuilder();
        summary.Append($"category={_categoryIndex} snapshot={_categoryRows.Length} visible={_displayToSource.Length} hidden={hiddenCount}");
        summary.Append($" filters=[{DescribeEnabledFilters()}]");
        var signature = summary.ToString();
        if (signature != _lastFilterSummary) {
            _lastFilterSummary = signature;
            LogFilterDebug(nameof(RebuildFilterMap), signature);
            if (hiddenCount > 0)
                LogHiddenItemDecisions();
        }
        LogPrePostFilterItemSets();
    }

    private void LogPrePostFilterItemSets() {
        var pre = FormatIndexedItemSet(Enumerable.Range(0, _categoryItemCount));
        if (pre != _lastPreFilter) {
            _lastPreFilter = pre;
            LogFilterDebug("pre-filter", pre);
        }
        var post = FormatIndexedItemSet(_displayToSource);
        if (post != _lastPostFilter) {
            _lastPostFilter = post;
            LogFilterDebug("post-filter", post);
        }
    }

    private string FormatIndexedItemSet(IEnumerable<int> sourceIndices) {
        var parts = new List<string>();
        foreach (var index in sourceIndices) {
            if ((uint)index >= (uint)_categoryItemCount)
                continue;
            parts.Add(FormatFilterRow(index, _categoryItemIds[index]));
        }
        return parts.Count == 0 ? "(none)" : string.Join("; ", parts);
    }

    private void LogHiddenItemDecisions() {
        var lines = new List<string>(_categoryItemCount);
        for (var i = 0; i < _categoryItemCount; i++) {
            var itemId = _categoryItemIds[i];
            if (!ShouldExcludeItem(itemId))
                continue;
            lines.Add($"{FormatFilterRow(i, itemId)} => {DescribeHideReasons(itemId)}");
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

    private static string FormatFilterRow(int index, uint itemId) {
        return $"[{index}] {(ItemHandle)itemId}";
    }
}
