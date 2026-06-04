using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using System.Text;

namespace GlamourLog.Features.PrismBox;

internal sealed unsafe class CrystallizeListHandler : IDisposable {
    private const string AddonName = "MiragePrismPrismBoxCrystallize";
    private const int MaxCategoryItems = 140;
    private const uint ItemTreeListNodeId = 11;
    private const int InventoryCategory = 0;
    private const int MaxHiddenItemLogLines = 48;
    private const int PrismBoxItemIdCount = 800;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly uint[] _prismBoxItemIdsSnapshot = new uint[PrismBoxItemIdCount];
    private bool _prismBoxItemIdsInitialized;
    private string? _lastFilterSummarySignature;
    private string? _lastPreFilterSignature;
    private string? _lastPostFilterSignature;
    private string? _lastHiddenItemsSignature;
    private string? _lastApplyPipelineSignature;
    private readonly AddonController<AtkUnitBase> _addonController;

    private PrismBoxCrystallizeItem[] _categoryRows = [];
    private int[] _displayToSource = [];
    private int _nativeCategoryItemCount;
    private int _crystallizeCategory = int.MinValue;
    private bool _needsCategorySnapshot;
    private bool _addonIsActive;
    private int _pendingFullListCount;

    private AtkValue[] _pristineAtkValues = [];
    private CrystallizeAtkSlot[] _atkLayout = [];
    private int[] _atkSlotRemap = [];
    private int _nativeAtkSlotCount;
    private int _filteredAtkSlotCount;

    public CrystallizeListHandler() {
        _filters = [
            new HideDresserDepositedFilter(),
            new HideArmoireEligibleFilter(),
            new HideNonOutfitItemsFilter(),
        ];

        _addonController = new AddonController<AtkUnitBase> {
            AddonName = AddonName,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnUpdate = OnAddonUpdate,
            OnFinalize = OnFinalize,
        };
        _addonController.Enable();
    }

    public void Dispose() {
        _addonIsActive = false;
        _addonController.Dispose();
        ClearFilterState();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool IncludeSectionHeaders
        => _crystallizeCategory != InventoryCategory && _displayToSource.Length > 0;

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(ApplyConfigChange);
    }

    private void ApplyConfigChange() {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName);
        var data = GetData();
        if (addon is null || data is null) {
            ClearFilterState();
            return;
        }

        if (!IsFilteringActive) {
            if (_categoryRows.Length > 0) {
                RestoreFullCategory(data);
                RepopulateTreeList(addon);
            }

            ClearFilterState();
            _pendingFullListCount = _categoryRows.Length > 0 ? _categoryRows.Length : InferCategoryItemCount(data);
            addon->OnRefresh(0, null);
            return;
        }

        if (!HasValidCategorySnapshot(data) && !TryCaptureCategorySnapshot(data))
            return;

        TryApplyFilterPipeline(addon, data);
        addon->OnRefresh(0, null);
    }

    private void OnPreRefresh(AtkUnitBase* addon) {
        _ = addon;
        if (!IsFilteringActive)
            return;

        var data = GetData();
        if (data is null)
            return;

        if (data->CrystallizeCategory != _crystallizeCategory) {
            _needsCategorySnapshot = true;
            _atkLayout = [];
            _nativeAtkSlotCount = 0;
            _filteredAtkSlotCount = 0;
        }

        EnsureCategoryTracked(data);

        if (HasValidCategorySnapshot(data) && _displayToSource.Length > 0)
            ProjectVisibleRows(data);
    }

    private void OnPostRefresh(AtkUnitBase* addon) {
        if (!IsAddonUsable(addon))
            return;

        _addonIsActive = true;
        var data = GetData();
        if (data is null)
            return;

        CachePristineAtk(addon->AtkValues, addon->AtkValuesCount, force: true);

        if (!IsFilteringActive) {
            if (_pendingFullListCount > 0)
                _pendingFullListCount = 0;
            return;
        }

        EnsureCategoryTracked(data);

        if (_needsCategorySnapshot || !HasValidCategorySnapshot(data)) {
            if (!TryCaptureCategorySnapshot(data))
                return;
            ParseAtkLayout(addon, force: true);
        }

        TryApplyFilterPipeline(addon, data);
    }

    private bool TryApplyFilterPipeline(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!_addonIsActive || !IsAddonUsable(addon) || _categoryRows.Length == 0)
            return false;

        EnsureAtkCached(addon);
        RebuildFilterMap();

        if (_displayToSource.Length == 0) {
            LogFilterDebug(nameof(TryApplyFilterPipeline), "applying empty category");
            ApplyEmptyCategory(data);
            RepopulateTreeList(addon);
            return true;
        }

        ProjectVisibleRows(data);
        RepopulateTreeList(addon);

        var tree = GetItemTreeList(addon);
        var itemsCount = tree is null ? 0 : tree->Items.Count;
        var listLength = tree is null ? 0 : ((AtkComponentList*)tree)->ListLength;
        var getItemCount = tree is null ? 0 : ((AtkComponentList*)tree)->GetItemCount();
        var applySummary =
            $"leaves={_displayToSource.Length} nativeAtkSlots={_nativeAtkSlotCount} filteredAtkSlots={_filteredAtkSlotCount} items.Count={itemsCount} listLength={listLength} getItemCount={getItemCount} agentCount={data->CrystallizeItemCount}";
        if (applySummary != _lastApplyPipelineSignature) {
            _lastApplyPipelineSignature = applySummary;
            LogFilterDebug(nameof(TryApplyFilterPipeline), $"applied {applySummary}");
        }

        return true;
    }

    private void OnFinalize(AtkUnitBase* addon) {
        _addonIsActive = false;
        if (addon is not null) {
            RestoreNativeTreeList(addon);

            var data = GetData();
            if (data is not null && _categoryRows.Length > 0)
                RestoreFullCategory(data);
        }

        ClearFilterState();
    }

    private void RestoreNativeTreeList(AtkUnitBase* addon) {
        if (_pristineAtkValues.Length == 0 || addon->AtkValues is null)
            return;

        CopyAtkValuesToAddon(addon, _pristineAtkValues);

        var tree = GetItemTreeList(addon);
        if (tree is null)
            return;

        var nativeCount = CrystallizeListAtk.InferItemCount(_pristineAtkValues);
        if (nativeCount <= 0)
            return;

        var list = (AtkComponentList*)tree;
        tree->LoadAtkValues(
            addon->AtkValuesCount,
            addon->AtkValues,
            CrystallizeListAtk.UintValuesOffset,
            CrystallizeListAtk.StringValuesOffset,
            CrystallizeListAtk.UintValuesPerItem,
            CrystallizeListAtk.StringValuesPerItem,
            nativeCount,
            list->CallBackInterface);
    }

    private void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        _displayToSource = [];
        _filteredAtkSlotCount = 0;
        data->CrystallizeItemCount = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private void ProjectVisibleRows(MiragePrismPrismBoxData* data) {
        var visible = _displayToSource.Length;
        for (var displayIndex = 0; displayIndex < visible; displayIndex++) {
            var sourceIndex = _displayToSource[displayIndex];
            if ((uint)sourceIndex >= (uint)_categoryRows.Length)
                continue;

            data->CrystallizeItems[displayIndex] = _categoryRows[sourceIndex];
        }

        data->CrystallizeItemCount = (ushort)visible;
        for (var i = visible; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private void RestoreFullCategory(MiragePrismPrismBoxData* data) {
        var count = _categoryRows.Length;
        for (var i = 0; i < count; i++)
            data->CrystallizeItems[i] = _categoryRows[i];

        data->CrystallizeItemCount = (ushort)count;
        for (var i = count; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;

        _displayToSource = [.. Enumerable.Range(0, count)];
    }

    private bool HasValidCategorySnapshot(MiragePrismPrismBoxData* data)
        => data is not null
           && !_needsCategorySnapshot
           && data->CrystallizeCategory == _crystallizeCategory
           && _categoryRows.Length > 0;

    private void RebuildFilterMap() {
        if (_categoryRows.Length == 0) {
            _displayToSource = [];
            _filteredAtkSlotCount = 0;
            LogFilterDebug("RebuildFilterMap", "skipped (empty category snapshot)");
            return;
        }

        var visible = new List<int>(_categoryRows.Length);
        for (var i = 0; i < _categoryRows.Length; i++) {
            if (_categoryRows[i].ItemId != 0 && !ShouldExcludeLeaf(_categoryRows[i].ItemId))
                visible.Add(i);
        }

        _displayToSource = [.. visible];
        UpdateFilteredAtkSlotCount();
        LogFilterRebuildSummary();
    }

    private bool ShouldExcludeLeaf(uint itemId)
        => _filters.Any(f => f.IsEnabled && f.ShouldHide(ItemUtil.GetBaseId(itemId).ItemId));

    private bool ShouldExcludeSourceIndex(int sourceIndex)
        => (uint)sourceIndex >= (uint)_categoryRows.Length || ShouldExcludeLeaf(_categoryRows[sourceIndex].ItemId);

    private Func<int, bool> BuildAtkHidePredicate()
        => _displayToSource.Length > 0 ? src => !_displayToSource.Contains(src) : ShouldExcludeSourceIndex;

    private void UpdateFilteredAtkSlotCount() {
        if (_displayToSource.Length == 0) {
            _filteredAtkSlotCount = 0;
            return;
        }

        if (_atkLayout.Length == 0) {
            _filteredAtkSlotCount = _displayToSource.Length;
            return;
        }

        _filteredAtkSlotCount = CrystallizeListAtk.CountVisibleSlots(
            _atkLayout,
            BuildAtkHidePredicate(),
            IncludeSectionHeaders);
    }

    private void EnsureAtkCached(AtkUnitBase* addon) {
        if (_pristineAtkValues.Length == 0 && addon is not null)
            CachePristineAtk(addon->AtkValues, addon->AtkValuesCount, force: true);

        if (_pristineAtkValues.Length == 0)
            return;

        if (_atkLayout.Length == 0)
            ParseAtkLayout(addon);
    }

    private void ParseAtkLayout(AtkUnitBase* addon, bool force = false) {
        if (_pristineAtkValues.Length == 0) {
            _atkLayout = [];
            _atkSlotRemap = [];
            _nativeAtkSlotCount = 0;
            return;
        }

        if (force || _atkLayout.Length == 0) {
            _atkSlotRemap = [];
            var inferred = CrystallizeListAtk.InferItemCount(_pristineAtkValues);
            var treeSlots = 0;
            if (addon is not null) {
                var tree = GetItemTreeList(addon);
                if (tree is not null)
                    treeSlots = ((AtkComponentList*)tree)->GetItemCount();
            }

            _nativeAtkSlotCount = Math.Max(inferred, treeSlots);
            _atkLayout = CrystallizeListAtk.Parse(_pristineAtkValues, _nativeAtkSlotCount, _categoryRows.Length);
        }
    }

    private void CachePristineAtk(AtkValue* source, int count, bool force = false) {
        if (source is null || count <= 0)
            return;

        if (!force && _pristineAtkValues.Length > 0 && !_needsCategorySnapshot)
            return;

        var copy = new AtkValue[count];
        for (var i = 0; i < count; i++)
            copy[i] = source[i];

        _pristineAtkValues = copy;
    }

    private void RepopulateTreeList(AtkUnitBase* addon) {
        if (addon is null)
            return;

        var tree = GetItemTreeList(addon);
        if (tree is null || _pristineAtkValues.Length == 0 || _atkLayout.Length == 0) {
            ApplyFilteredListLayout(addon);
            return;
        }

        var workingAtk = CrystallizeListAtk.Clone(_pristineAtkValues);
        if (IsFilteringActive) {
            _filteredAtkSlotCount = CrystallizeListAtk.ApplyToBuffer(
                workingAtk,
                _atkLayout,
                BuildAtkHidePredicate(),
                CrystallizeListAtk.UintValuesOffset,
                CrystallizeListAtk.StringValuesOffset,
                CrystallizeListAtk.UintValuesPerItem,
                CrystallizeListAtk.StringValuesPerItem,
                _nativeAtkSlotCount,
                IncludeSectionHeaders,
                _atkSlotRemap);
        }

        if (_filteredAtkSlotCount <= 0) {
            ApplyFilteredListLayout(addon);
            return;
        }

        if (addon->AtkValues is null)
            return;

        CopyAtkValuesToAddon(addon, workingAtk);

        var list = (AtkComponentList*)tree;
        tree->LoadAtkValues(
            addon->AtkValuesCount,
            addon->AtkValues,
            CrystallizeListAtk.UintValuesOffset,
            CrystallizeListAtk.StringValuesOffset,
            CrystallizeListAtk.UintValuesPerItem,
            CrystallizeListAtk.StringValuesPerItem,
            _filteredAtkSlotCount,
            list->CallBackInterface);

        SyncTreeItemsFromAtk(tree, workingAtk, _filteredAtkSlotCount);
        RefreshTreeListLayout(addon);
    }

    private static void SyncTreeItemsFromAtk(AtkComponentTreeList* tree, AtkValue[] atkValues, int slotCount) {
        if (slotCount <= 0)
            return;

        var itemCount = Math.Min(slotCount, tree->Items.Count);
        for (var slot = 0; slot < itemCount; slot++) {
            var item = tree->GetItem(slot);
            if (item is null)
                continue;

            CrystallizeListAtk.CopySlotToTreeItem(atkValues, slot, item);
        }
    }

    private static void CopyAtkValuesToAddon(AtkUnitBase* addon, AtkValue[] source) {
        var count = Math.Min(addon->AtkValuesCount, source.Length);
        for (var i = 0; i < count; i++)
            addon->AtkValues[i] = source[i];
    }

    private void ApplyFilteredListLayout(AtkUnitBase* addon) {
        var tree = GetItemTreeList(addon);
        if (tree is null)
            return;

        var list = (AtkComponentList*)tree;
        var count = (short)(_filteredAtkSlotCount > 0 ? _filteredAtkSlotCount : tree->Items.Count);
        _filteredAtkSlotCount = count;
        if (count <= 0)
            return;

        list->SetItemCount(0);
        list->SetItemCount(count);
        RefreshTreeListLayout(addon);
    }

    private static void RefreshTreeListLayout(AtkUnitBase* addon) {
        var tree = GetItemTreeList(addon);
        if (tree is null)
            return;

        var list = (AtkComponentList*)tree;
        list->UpdateListItems();
        list->RecalculateVisibleItems(true);
        tree->LayoutRefreshPending = true;
        list->IsUpdatePending = true;
        list->IsScrollRefreshPending = true;
    }

    private bool TryCaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        EnsureCategoryTracked(data);
        if (InferCategoryItemCount(data) <= 0)
            return false;

        CaptureCategorySnapshot(data);
        return true;
    }

    private void EnsureCategoryTracked(MiragePrismPrismBoxData* data) {
        if (_crystallizeCategory == data->CrystallizeCategory && _nativeCategoryItemCount > 0)
            return;

        _crystallizeCategory = data->CrystallizeCategory;
        _nativeCategoryItemCount = InferCategoryItemCount(data);
        _needsCategorySnapshot = true;
        _lastFilterSummarySignature = null;
        _lastPreFilterSignature = null;
        _lastPostFilterSignature = null;
        _lastHiddenItemsSignature = null;
        LogFilterDebug(nameof(EnsureCategoryTracked), $"category -> {_crystallizeCategory} nativeRows={_nativeCategoryItemCount}");
    }

    private void CaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        var agentCount = _nativeCategoryItemCount > 0 ? _nativeCategoryItemCount : InferCategoryItemCount(data);
        if (agentCount <= 0) {
            _categoryRows = [];
            _displayToSource = [];
            _needsCategorySnapshot = false;
            return;
        }

        if (IsFilteringActive && _categoryRows.Length > 0 && agentCount < _categoryRows.Length) {
            _needsCategorySnapshot = false;
            LogFilterDebug(nameof(CaptureCategorySnapshot),
                $"skipped (agentCount={agentCount} < snapshot={_categoryRows.Length})");
            return;
        }

        _nativeCategoryItemCount = agentCount;
        _categoryRows = new PrismBoxCrystallizeItem[agentCount];
        for (var i = 0; i < agentCount; i++)
            _categoryRows[i] = data->CrystallizeItems[i];

        _crystallizeCategory = data->CrystallizeCategory;
        _needsCategorySnapshot = false;

        LogFilterDebug(nameof(CaptureCategorySnapshot),
            $"category={_crystallizeCategory} agentRows={agentCount}");
    }

    private static MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }

    private static int InferCategoryItemCount(MiragePrismPrismBoxData* data) {
        if (data->CrystallizeItemCount > 0)
            return data->CrystallizeItemCount;

        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }

        return lastIndex >= 0 ? lastIndex + 1 : 0;
    }

    private void OnAddonUpdate(AtkUnitBase* addon) {
        if (!_addonIsActive || !IsAddonUsable(addon) || !IsFilteringActive)
            return;

        var mirage = MirageManager.Instance();
        if (mirage is null || !TryDetectPrismBoxItemIdsChange(mirage))
            return;

        var data = GetData();
        if (data is not null && _categoryRows.Length > 0)
            RestoreFullCategory(data);

        _needsCategorySnapshot = true;
        addon->OnRefresh(0, null);
    }

    private bool TryDetectPrismBoxItemIdsChange(MirageManager* mirage) {
        var current = mirage->PrismBoxItemIds;
        if (!_prismBoxItemIdsInitialized) {
            current.CopyTo(_prismBoxItemIdsSnapshot);
            _prismBoxItemIdsInitialized = true;
            return false;
        }

        if (current.SequenceEqual(_prismBoxItemIdsSnapshot))
            return false;

        current.CopyTo(_prismBoxItemIdsSnapshot);
        return true;
    }

    private void ClearFilterState() {
        _categoryRows = [];
        _displayToSource = [];
        _pristineAtkValues = [];
        _atkLayout = [];
        _atkSlotRemap = [];
        _nativeAtkSlotCount = 0;
        _filteredAtkSlotCount = 0;
        _nativeCategoryItemCount = 0;
        _crystallizeCategory = int.MinValue;
        _needsCategorySnapshot = false;
        _pendingFullListCount = 0;
        _addonIsActive = false;
        _prismBoxItemIdsInitialized = false;
        _lastFilterSummarySignature = null;
        _lastPreFilterSignature = null;
        _lastPostFilterSignature = null;
        _lastHiddenItemsSignature = null;
        _lastApplyPipelineSignature = null;
    }

    private static bool IsAddonUsable(AtkUnitBase* addon)
        => addon is not null && addon->IsVisible;

    private static AtkComponentTreeList* GetItemTreeList(AtkUnitBase* addon) {
        if (addon is null)
            return null;

        var node = addon->GetNodeById(ItemTreeListNodeId);
        return node is null ? null : node->GetAsAtkComponentTreeList();
    }

    private static void LogFilterDebug(string phase, string message)
        => Svc.Log.Information($"[{nameof(CrystallizeListHandler)}.{phase}] {message}");

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
