using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Text;

namespace GlamourLog.Features.PrismBox;

internal sealed unsafe partial class CrystallizeListHandler {
    private const int MaxHiddenItemLogLines = 48;

    // signature dedupe so identical state isn't logged every frame
    private string? _lastFilterSummarySignature; // RebuildFilterMap summary
    private string? _lastPreFilterSignature; // full snapshot item list
    private string? _lastPostFilterSignature; // visible item list
    private string? _lastHiddenItemsSignature; // per-row hide reasons
    private string? _lastApplyPipelineSignature; // post-apply slot counts
    private string? _lastFilterOffStateSignature; // filters-disabled tree state
    private string? _lastFilterOnStateSignature; // filters-enabled tree state

    private void ClearFilterLogSignatures() {
        _lastFilterSummarySignature = null;
        _lastPreFilterSignature = null;
        _lastPostFilterSignature = null;
        _lastHiddenItemsSignature = null;
        _lastApplyPipelineSignature = null;
        _lastFilterOffStateSignature = null;
        _lastFilterOnStateSignature = null;
    }

    private static void LogFilterDebug(string phase, string message)
        => Svc.Log.Information($"[{nameof(CrystallizeListHandler)}.{phase}] {message}");

    private void LogNativeListMetrics(AtkUnitBase* addon) {
        if (addon is null)
            return;
        ResolveNativeTree(addon);
        if (_nativeTreeList is null)
            return;
        var list = (AtkComponentList*)_nativeTreeList;
        LogFilterDebug(nameof(LogNativeListMetrics),
            $"native itemHeight={list->ItemHeight} rowStepY={list->RowStepY} listHeight={list->ListHeight} numVisible={list->NumVisibleItems} numVisibleRows={list->NumVisibleRows} listLength={list->ListLength}");
    }

    private void LogApplyPipelineResult(AtkComponentTreeList* tree, MiragePrismPrismBoxData* data) {
        var itemsCount = tree->Items.Count;
        var listLength = ((AtkComponentList*)tree)->ListLength;
        var getItemCount = ((AtkComponentList*)tree)->GetItemCount();
        var applySummary = $"leaves={_displayToSource.Length} nativeAtkSlots={_nativeAtkSlotCount} filteredAtkSlots={_filteredAtkSlotCount} items.Count={itemsCount} listLength={listLength} getItemCount={getItemCount} agentCount={data->CrystallizeItemCount} filterFlags={_crystallizeFilterFlagsSnapshot} flagsChangedThisRefresh={_crystallizeFilterFlagsChangedThisRefresh}";
        if (applySummary == _lastApplyPipelineSignature)
            return;
        _lastApplyPipelineSignature = applySummary;
        LogFilterDebug(nameof(ApplyFilterAfterNativeRefresh), $"applied {applySummary}");
    }

    private void LogFilterOnState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        ResolveNativeTree(addon);
        var tree = _nativeTreeList;
        var nativeVisible = _nativeTreeResNode is not null && _nativeTreeResNode->IsVisible();
        var items = tree is not null ? tree->Items.Count : -1;
        var listLength = tree is not null ? ((AtkComponentList*)tree)->ListLength : -1;
        var numVisible = tree is not null ? ((AtkComponentList*)tree)->NumVisibleItems : (short)-1;
        var itemHeight = tree is not null ? ((AtkComponentList*)tree)->ItemHeight : (short)-1;
        var listHeight = tree is not null ? ((AtkComponentList*)tree)->ListHeight : (short)-1;
        var hasRenderer = tree is not null && ((AtkComponentList*)tree)->FirstAtkComponentListItemRenderer is not null;
        var hasScrollBar = tree is not null && ((AtkComponentList*)tree)->ScrollBarComponent is not null;
        var summary =
            $"leaves={_displayToSource.Length} agentCount={data->CrystallizeItemCount} " +
            $"nativeNodeId={(_nativeTreeResNode is not null ? _nativeTreeResNode->NodeId : 0u)} nativeVisible={nativeVisible} " +
            $"nativeItems={items} nativeListLength={listLength} nativeNumVisible={numVisible} " +
            $"itemHeight={itemHeight} listHeight={listHeight} hasRenderer={hasRenderer} hasScrollBar={hasScrollBar}";
        if (summary == _lastFilterOnStateSignature)
            return;
        _lastFilterOnStateSignature = summary;
        LogFilterDebug(phase, summary);
    }

    private void LogFilterOffState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        ResolveNativeTree(addon);
        var nativeVisible = _nativeTreeResNode is not null && _nativeTreeResNode->IsVisible();
        var nativeItems = _nativeTreeList is not null ? _nativeTreeList->Items.Count : -1;
        var nativeListLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : (short)-1;
        var nativeGetItemCount = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->GetItemCount() : -1;
        var summary =
            $"snapshot={_categoryRows.Length} agentCount={data->CrystallizeItemCount} inferred={InferCategoryItemCount(data)} " +
            $"nativeNodeId={(_nativeTreeResNode is not null ? _nativeTreeResNode->NodeId : 0u)} " +
            $"nativeVisible={nativeVisible} nativeItems={nativeItems} nativeListLength={nativeListLength} nativeGetItemCount={nativeGetItemCount} " +
            $"nativeAtkSlots={_nativeAtkSlotCount} addonAtkValues={addon->AtkValuesCount}";
        if (summary == _lastFilterOffStateSignature)
            return;
        _lastFilterOffStateSignature = summary;
        LogFilterDebug(phase, summary);
    }

    private void LogFilterRebuildSummary() {
        var hiddenCount = _categoryRows.Length - _displayToSource.Length;
        var summary = new StringBuilder();
        summary.Append($"category={_crystallizeCategory} snapshot={_categoryRows.Length} visible={_displayToSource.Length} hidden={hiddenCount}");
        summary.Append($" filters=[{DescribeEnabledFilters()}]");
        var signature = summary.ToString();
        if (signature != _lastFilterSummarySignature) {
            _lastFilterSummarySignature = signature;
            LogFilterDebug(nameof(RebuildFilterMap), signature);
            if (hiddenCount > 0)
                LogHiddenItemDecisions();
        }
        LogPrePostFilterItemSets();
    }

    private void LogPrePostFilterItemSets() {
        var pre = FormatIndexedItemSet(Enumerable.Range(0, _categoryRows.Length));
        if (pre != _lastPreFilterSignature) {
            _lastPreFilterSignature = pre;
            LogFilterDebug("pre-filter", pre);
        }
        var post = FormatIndexedItemSet(_displayToSource);
        if (post != _lastPostFilterSignature) {
            _lastPostFilterSignature = post;
            LogFilterDebug("post-filter", post);
        }
    }

    private string FormatIndexedItemSet(IEnumerable<int> sourceIndices) {
        var parts = new List<string>();
        foreach (var index in sourceIndices) {
            if ((uint)index >= (uint)_categoryRows.Length)
                continue;
            parts.Add(FormatFilterRow(index, _categoryRows[index].ItemId));
        }
        return parts.Count == 0 ? "(none)" : string.Join("; ", parts);
    }

    private void LogHiddenItemDecisions() {
        var lines = new List<string>(_categoryRows.Length);
        for (var i = 0; i < _categoryRows.Length; i++) {
            var itemId = _categoryRows[i].ItemId;
            if (!ShouldExcludeLeaf(itemId))
                continue;
            lines.Add($"{FormatFilterRow(i, itemId)} => {DescribeHideReasons(itemId)}");
        }
        var signature = string.Join('\n', lines);
        if (signature == _lastHiddenItemsSignature)
            return;
        _lastHiddenItemsSignature = signature;
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

    private static string FilterDebugLabel(IPrismBoxRowFilter filter) => filter switch {
        HideDresserDepositedFilter => "dresser",
        HideArmoireEligibleFilter => "armoire",
        HideNonOutfitItemsFilter => "non-outfit",
        _ => filter.GetType().Name,
    };

    private string DescribeHideReasons(uint rawItemId) {
        if (rawItemId == 0)
            return "empty slot";
        var baseId = ItemUtil.GetBaseId(rawItemId).ItemId;
        var reasons = new List<string>();
        foreach (var filter in _filters) {
            if (!filter.IsEnabled || !filter.ShouldHide(baseId))
                continue;
            reasons.Add(FilterDebugLabel(filter));
        }
        if (reasons.Count == 0)
            reasons.Add("no matching filter (unexpected)");
        return string.Join(", ", reasons);
    }

    private static string FormatFilterRow(int index, uint rawItemId) {
        var ids = FormatItemIds(rawItemId);
        try {
            var baseId = ItemUtil.GetBaseId(rawItemId).ItemId;
            var name = Item.GetRow(baseId).Name.ToString().Trim();
            return $"[{index}] {ids} name=\"{name}\"";
        }
        catch {
            return $"[{index}] {ids}";
        }
    }

    private static string FormatItemIds(uint rawItemId) {
        var baseId = ItemUtil.GetBaseId(rawItemId).ItemId;
        return $"itemId={rawItemId} baseId={baseId}";
    }
}
