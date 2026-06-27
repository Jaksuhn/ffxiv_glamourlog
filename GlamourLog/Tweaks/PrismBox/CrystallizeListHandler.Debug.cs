using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Text;

namespace GlamourLog.Features.PrismBox;

// verbose filter debug logging for CrystallizeListHandler (signatures dedupe repeated lines)
internal sealed partial class CrystallizeListHandler {
    private const int MaxHiddenItemLogLines = 48;

    private string? _lastFilterSummary;
    private string? _lastPreFilter;
    private string? _lastPostFilter;
    private string? _lastHiddenItems;
    private string? _lastApplyPipeline;
    private string? _lastFilterOffState;
    private string? _lastFilterOnState;
    private string? _lastSnapshotUnavailableSignature;
    private string? _lastNativePopulate;
    private string? _lastMissingAgentData;
    private bool _filterFlagsChangedThisRefresh;

    private void ResetFilterFlagsChangedThisRefresh()
        => _filterFlagsChangedThisRefresh = false;

    private void MarkFilterFlagsChangedThisRefresh()
        => _filterFlagsChangedThisRefresh = true;

    private void ClearFilterLogSignatures() {
        _lastFilterSummary = null;
        _lastPreFilter = null;
        _lastPostFilter = null;
        _lastHiddenItems = null;
        _lastApplyPipeline = null;
        _lastFilterOffState = null;
        _lastFilterOnState = null;
        _lastSnapshotUnavailableSignature = null;
        _lastNativePopulate = null;
        _lastMissingAgentData = null;
    }

    private unsafe void LogMissingAgentDataOnce() {
        var agent = AgentMiragePrismPrismBox.Instance();
        var signature = agent is null ? "agent=null" : "agent ok, data=null";
        if (signature == _lastMissingAgentData)
            return;
        _lastMissingAgentData = signature;
        LogFilterDebug(nameof(OnPostRefresh), $"cannot read prism box data ({signature})");
    }

    private static void LogFilterDebug(string phase, string message)
        => Svc.Log.Information($"[{nameof(CrystallizeListHandler)}.{phase}] {message}");

    private unsafe void LogNativeTreePopulated(MiragePrismPrismBoxData* data, int nativeItemCount) {
        var scanned = ScanPopulatedCategoryItemCount(data);
        var summary =
            $"nativeItemCount={nativeItemCount} cat={data->CrystallizeCategory} agentReported={data->CrystallizeItemCount} agentScanned={scanned} " +
            $"snapshotRows={_categoryRows.Length} needsSnapshot={_needsCategorySnapshot}";
        if (summary == _lastNativePopulate)
            return;
        _lastNativePopulate = summary;
        LogFilterDebug(nameof(OnNativeTreePopulated), summary);
    }

    private unsafe void LogSnapshotUnavailableOnce(MiragePrismPrismBoxData* data) {
        var reported = data->CrystallizeItemCount;
        var scanned = InferPopulatedCategoryItemCount(data);
        var signature = $"cat={data->CrystallizeCategory} reported={reported} scanned={scanned}";
        if (signature == _lastSnapshotUnavailableSignature)
            return;
        _lastSnapshotUnavailableSignature = signature; // one line per distinct wait state
        LogFilterDebug(nameof(OnPostRefresh),
            $"category snapshot unavailable after native refresh ({signature})");
    }

    private unsafe void LogApplyPipelineResult(AtkComponentTreeList* tree, MiragePrismPrismBoxData* data) {
        var itemsCount = tree->Items.Count;
        var listLength = ((AtkComponentList*)tree)->ListLength;
        var getItemCount = ((AtkComponentList*)tree)->GetItemCount();
        var applySummary =
            $"leaves={_displayToSource.Length} nativeAtkSlots={_nativeTree.NativeSlotCount} filteredAtkSlots={_nativeTree.FilteredSlotCount} " +
            $"items.Count={itemsCount} listLength={listLength} getItemCount={getItemCount} agentCount={data->CrystallizeItemCount} " +
            $"filterFlags={_crystallizeFilterFlagsSnapshot} flagsChangedThisRefresh={_filterFlagsChangedThisRefresh}";
        if (applySummary == _lastApplyPipeline)
            return;
        _lastApplyPipeline = applySummary;
        LogFilterDebug(nameof(TryApplyFilterPipeline), $"applied {applySummary}");
    }

    private unsafe void LogFilterOnState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _nativeTree.Resolve(addon);
        var tree = _nativeTree.TreeList;
        var nativeVisible = tree is not null && ((AtkResNode*)tree)->IsVisible();
        var items = tree is not null ? tree->Items.Count : -1;
        var listLength = tree is not null ? ((AtkComponentList*)tree)->ListLength : -1;
        var numVisible = tree is not null ? ((AtkComponentList*)tree)->NumVisibleItems : (short)-1;
        var itemHeight = tree is not null ? ((AtkComponentList*)tree)->ItemHeight : (short)-1;
        var listHeight = tree is not null ? ((AtkComponentList*)tree)->ListHeight : (short)-1;
        var hasRenderer = tree is not null && ((AtkComponentList*)tree)->FirstAtkComponentListItemRenderer is not null;
        var hasScrollBar = tree is not null && ((AtkComponentList*)tree)->ScrollBarComponent is not null;
        var summary =
            $"leaves={_displayToSource.Length} agentCount={data->CrystallizeItemCount} " +
            $"nativeVisible={nativeVisible} nativeItems={items} nativeListLength={listLength} nativeNumVisible={numVisible} " +
            $"itemHeight={itemHeight} listHeight={listHeight} hasRenderer={hasRenderer} hasScrollBar={hasScrollBar}";
        if (summary == _lastFilterOnState)
            return;
        _lastFilterOnState = summary;
        LogFilterDebug(phase, summary);
    }

    private unsafe void LogFilterOffState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _nativeTree.Resolve(addon);
        var tree = _nativeTree.TreeList;
        var nativeVisible = tree is not null && ((AtkResNode*)tree)->IsVisible();
        var nativeItems = tree is not null ? tree->Items.Count : -1;
        var nativeListLength = tree is not null ? ((AtkComponentList*)tree)->ListLength : -1;
        var nativeGetItemCount = tree is not null ? ((AtkComponentList*)tree)->GetItemCount() : -1;
        var summary =
            $"snapshot={_categoryRows.Length} agentCount={data->CrystallizeItemCount} populated={InferPopulatedCategoryItemCount(data)} " +
            $"nativeVisible={nativeVisible} nativeItems={nativeItems} nativeListLength={nativeListLength} nativeGetItemCount={nativeGetItemCount} " +
            $"nativeAtkSlots={_nativeTree.NativeSlotCount} addonAtkValues={addon->AtkValuesCount}";
        if (summary == _lastFilterOffState)
            return;
        _lastFilterOffState = summary;
        LogFilterDebug(phase, summary);
    }

    private void LogFilterRebuildSummary() {
        var hiddenCount = _categoryRows.Length - _displayToSource.Length;
        var summary = new StringBuilder();
        summary.Append($"category={_crystallizeCategory} snapshot={_categoryRows.Length} visible={_displayToSource.Length} hidden={hiddenCount}");
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
        var pre = FormatIndexedItemSet(Enumerable.Range(0, _categoryRows.Length));
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
