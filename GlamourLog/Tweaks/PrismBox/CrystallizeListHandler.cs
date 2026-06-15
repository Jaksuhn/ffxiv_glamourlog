using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;

namespace GlamourLog.Features.PrismBox;

// row filters for MiragePrismPrismBoxCrystallize — patch agent + node 11 in place.
// snapshot _categoryRows -> filter -> ProjectVisibleRows -> RepopulateDisplayTreeList; OnPreRefresh restores when safe.
internal sealed unsafe partial class CrystallizeListHandler : IDisposable {
    private const string AddonName = "MiragePrismPrismBoxCrystallize";
    private const int MaxCategoryItems = 140;
    private const uint ItemTreeListNodeId = 11;
    private const int PrismBoxItemIdCount = 800;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly AddonController<AtkUnitBase> _addonController;
    private readonly Hook<AtkComponentTreeList.Delegates.LoadAtkValues>? _loadAtkValuesHook;
    private readonly uint[] _prismBoxItemIdsSnapshot = new uint[PrismBoxItemIdCount];

    private bool _prismBoxItemIdsInitialized; // baseline for PrismBoxItemIds change detection
    private byte _crystallizeFilterFlagsSnapshot = byte.MaxValue; // last CrystallizeFilterFlags
    private PrismBoxCrystallizeItem[] _categoryRows = []; // full tab; index = agent source slot
    private int[] _displayToSource = []; // source indices to show after filtering
    private int _nativeCategoryItemCount; // row count at category track time
    private int _crystallizeCategory = int.MinValue; // CrystallizeCategory tab id
    private bool _needsCategorySnapshot; // recapture _categoryRows on next PostRefresh
    private bool _crystallizeFilterFlagsChangedThisRefresh; // gearset filter toggled this refresh
    private int _refreshRecursionDepth; // guard re-entrant OnRefresh during filter repair
    private AtkValue[] _nativeAtkSnapshot = []; // addon ATK buffer after native refresh
    private CrystallizeAtkSlot[] _atkLayout = []; // header/leaf + source index per ATK slot
    private int _nativeAtkSlotCount; // populated slots in _nativeAtkSnapshot
    private int _filteredAtkSlotCount; // slots after ApplyToBuffer
    private AtkResNode* _nativeTreeResNode; // node 11 — filtered in place
    private AtkComponentTreeList* _nativeTreeList;
    private CrystallizeAtkBufferLayout _atkBufferLayout;

    private bool HasAtkBufferLayout => _atkBufferLayout.IsValid;

    public CrystallizeListHandler() {
        _filters = [
            new HideDresserDepositedFilter(),
            new HideArmoireEligibleFilter(),
            new HideNonOutfitItemsFilter(),
        ];
        _loadAtkValuesHook = Svc.Hook.HookFromAddress<AtkComponentTreeList.Delegates.LoadAtkValues>(
            (nint)AtkComponentTreeList.MemberFunctionPointers.LoadAtkValues,
            LoadAtkValuesDetour);
        _loadAtkValuesHook.Enable();

        _addonController = new AddonController<AtkUnitBase> {
            AddonName = AddonName,
            OnSetup = OnSetup,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnUpdate = OnAddonUpdate,
            OnFinalize = OnFinalize,
        };
        _addonController.Enable();
    }

    public void Dispose() {
        _addonController.Dispose();
        _loadAtkValuesHook?.Dispose();
        ClearFilterState();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool IncludeSectionHeaders => _displayToSource.Length > 0;

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(ApplyConfigChange);
    }

    // settings toggle: restore full category, refilter in place or cold refresh
    private void ApplyConfigChange() {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName);
        var data = GetData();
        if (addon is null || data is null) {
            ClearFilterState();
            return;
        }
        if (!IsFilteringActive) {
            var agentCountBefore = data->CrystallizeItemCount;
            var snapshotCount = _categoryRows.Length;
            if (snapshotCount > 0)
                RestoreFullCategory(data);
            LogFilterDebug(nameof(ApplyConfigChange),
                $"filters disabled snapshot={snapshotCount} agentCountBefore={agentCountBefore} agentCountAfter={data->CrystallizeItemCount} restored={(snapshotCount > 0)}");
            _displayToSource = [];
            _filteredAtkSlotCount = 0;
            ClearFilterLogSignatures();
            addon->OnRefresh(0, null);
            return;
        }

        EnsureCategoryTracked(data);

        if (_categoryRows.Length > 0)
            RestoreFullCategory(data);

        // fast path: existing snapshot + ATK
        if (HasValidCategorySnapshot(data) && TryApplyFilterPipeline(addon, data)) {
            ClearFilterLogSignatures();
            LogFilterDebug(nameof(ApplyConfigChange),
                $"filters refiltered in place category={_crystallizeCategory} snapshot={_categoryRows.Length} visible={_displayToSource.Length}");
            return;
        }

        if (!HasValidCategorySnapshot(data) && !TryCaptureCategorySnapshot(data)) {
            _needsCategorySnapshot = true;
            LogFilterDebug(nameof(ApplyConfigChange),
                $"filters enabled, awaiting category snapshot category={data->CrystallizeCategory} snapshot={_categoryRows.Length}");
            addon->OnRefresh(0, null);
            return;
        }

        InvalidateNativeAtkCache();
        _needsCategorySnapshot = _categoryRows.Length > 0;
        ClearFilterLogSignatures();
        LogFilterDebug(nameof(ApplyConfigChange),
            $"filters enabled category={_crystallizeCategory} snapshot={_categoryRows.Length} needsSnapshot={_needsCategorySnapshot}");
        addon->OnRefresh(0, null);
    }

    private bool TryApplyFilterPipeline(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_categoryRows.Length == 0 || !HasAtkBufferLayout || _nativeAtkSnapshot.Length == 0)
            return false;

        ResolveNativeTree(addon);
        if (_nativeTreeList is null)
            return false;

        if (_atkLayout.Length == 0 || _nativeAtkSlotCount <= 0)
            ParseAtkLayout(force: true);

        if (_atkLayout.Length == 0 || _nativeAtkSlotCount <= 0)
            return false;

        ApplyFilterAfterNativeRefresh(addon, data);
        return true;
    }

    private void OnSetup(AtkUnitBase* addon) {
        InvalidateTreeNodeCache();
        EnsureNativeTreeVisible(addon);
    }

    // restore full category before native refresh when agent still holds the filtered projection
    private void OnPreRefresh(AtkUnitBase* addon) {
        var data = GetData();
        if (data is null)
            return;

        _crystallizeFilterFlagsChangedThisRefresh = false;
        if (IsFilteringActive) {
            EnsureNativeTreeVisible(addon);
            EnsureCategoryTracked(data);
            if (TryDetectCrystallizeFilterFlagsChange(data, nameof(OnPreRefresh))) {
                InvalidateNativeAtkCache();
                _needsCategorySnapshot = true;
                _nativeCategoryItemCount = 0;
            }
        }

        if (_categoryRows.Length > 0 && !_crystallizeFilterFlagsChangedThisRefresh) {
            if (ShouldRestoreFullCategoryBeforeNativeRefresh(data)) {
                var agentCountBefore = data->CrystallizeItemCount;
                RestoreFullCategory(data);
                if (!IsFilteringActive) {
                    LogFilterDebug(nameof(OnPreRefresh),
                        $"restored full category before native refresh snapshot={_categoryRows.Length} agentCountBefore={agentCountBefore} agentCountAfter={data->CrystallizeItemCount}");
                }
            }
            return;
        }

        // CrystallizeFilterFlags changed — don't restore stale snapshot over native row set
        if (_categoryRows.Length > 0 && _crystallizeFilterFlagsChangedThisRefresh) {
            LogFilterDebug(nameof(OnPreRefresh),
                $"skipped restore (filter flags changed) snapshot={_categoryRows.Length} agentCount={data->CrystallizeItemCount} filterFlags={data->CrystallizeFilterFlags}");
            return;
        }

        if (!IsFilteringActive) {
            LogFilterDebug(nameof(OnPreRefresh),
                $"no category snapshot to restore agentCount={data->CrystallizeItemCount} inferred={InferCategoryItemCount(data)}");
        }
    }

    // capture native list, then apply filters
    private void OnPostRefresh(AtkUnitBase* addon) {
        if (!IsAddonUsable(addon))
            return;

        var data = GetData();
        if (data is null)
            return;

        _refreshRecursionDepth++;
        try {
            OnPostRefreshCore(addon, data);
        }
        finally {
            _refreshRecursionDepth--;
        }
    }

    private void OnPostRefreshCore(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!IsFilteringActive) {
            ResolveNativeTree(addon);
            CaptureNativeAtkSnapshot(addon);
            if (_categoryRows.Length > 0 && HasAtkBufferLayout && _nativeAtkSnapshot.Length > 0)
                ParseAtkLayout(force: true);
            EnsureNativeTreeVisible(addon);
            LogFilterOffState(nameof(OnPostRefresh), addon, data);
            return;
        }

        ResolveNativeTree(addon);
        EnsureCategoryTracked(data);
        TryDetectCrystallizeFilterFlagsChange(data, nameof(OnPostRefresh));
        EnsureNativeTreeVisible(addon);
        CaptureNativeAtkSnapshot(addon);
        if (_needsCategorySnapshot || _crystallizeFilterFlagsChangedThisRefresh) {
            if (!TryCaptureCategorySnapshotAfterNative(data)) {
                LogFilterDebug(nameof(OnPostRefresh), "filter enable aborted (category snapshot unavailable after native refresh)");
                return;
            }
        }
        else {
            ParseAtkLayout(force: true);
        }
        if (!HasAtkBufferLayout || _nativeAtkSnapshot.Length == 0 || _atkLayout.Length == 0 || _categoryRows.Length == 0) {
            LogFilterDebug(nameof(OnPostRefresh),
                $"filter enable aborted (atkBufferLayout={HasAtkBufferLayout} nativeAtk={_nativeAtkSnapshot.Length} layout={_atkLayout.Length} snapshot={_categoryRows.Length})");
            return;
        }
        if (IsNativeTreeTruncatedVersusSnapshot(data)) {
            RestoreFullCategory(data);
            var listLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : 0;
            LogFilterDebug(nameof(OnPostRefresh),
                $"native tree truncated vs snapshot (listLength={listLength} snapshot={_categoryRows.Length}) — requesting refresh");
            RequestAddonRefresh(addon);
            return;
        }
        ApplyFilterAfterNativeRefresh(addon, data);
        LogFilterOnState(nameof(OnPostRefresh), addon, data);
    }

    // map -> ProjectVisibleRows -> compact ATK -> reload node 11
    private void ApplyFilterAfterNativeRefresh(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_nativeTreeList is null)
            return;

        RebuildFilterMap();
        if (_displayToSource.Length == 0) {
            LogFilterDebug(nameof(ApplyFilterAfterNativeRefresh), "applying empty category");
            ApplyEmptyCategory(data);
            RepopulateDisplayTreeList(addon);
            return;
        }

        ProjectVisibleRows(data);
        RepopulateDisplayTreeList(addon);
        LogApplyPipelineResult(_nativeTreeList, data);
    }

    private void OnFinalize(AtkUnitBase* addon) {
        if (addon is not null) {
            EnsureNativeTreeVisible(addon);
            var data = GetData();
            if (data is not null && _categoryRows.Length > 0)
                RestoreFullCategory(data);
        }
        InvalidateTreeNodeCache();
        ClearFilterState();
    }

    private void InvalidateTreeNodeCache() {
        _nativeTreeResNode = null;
        _nativeTreeList = null;
    }

    private void ResolveNativeTree(AtkUnitBase* addon) {
        var nativeNode = addon->GetNodeById(ItemTreeListNodeId);
        if (nativeNode is null)
            return;
        _nativeTreeResNode = nativeNode;
        _nativeTreeList = nativeNode->GetAsAtkComponentTreeList();
    }

    // show node 11, hide stray duplicate tree nodes
    private void EnsureNativeTreeVisible(AtkUnitBase* addon) {
        ResolveNativeTree(addon);
        ApplyItemTreeVisibility(addon, _nativeTreeResNode);
    }

    private void ApplyItemTreeVisibility(AtkUnitBase* addon, AtkResNode* activeNode) {
        ResolveNativeTree(addon);
        foreach (var nodePtr in addon->UldManager.Nodes) {
            var node = nodePtr.Value;
            var tree = node is not null ? node->GetAsAtkComponentTreeList() : null;
            if (tree is null)
                continue;
            if (node != _nativeTreeResNode && !node->IsDuplicatedNode())
                continue;
            var visible = activeNode is not null && node == activeNode;
            SetNodeVisible(node, visible);
            SetTreeScrollBarVisible(tree, visible);
            SetTreeInteractionEnabled(tree, visible);
        }
        addon->UldManager.UpdateDrawNodeList();
    }

    private static void SetNodeVisible(AtkResNode* node, bool visible) {
        if (node is null)
            return;
        node->ToggleVisibility(visible);
    }

    private static void SetTreeInteractionEnabled(AtkComponentTreeList* tree, bool enabled) {
        if (tree is null)
            return;
        ((AtkComponentList*)tree)->IsItemInteractionEnabled = enabled;
    }

    private static void SetTreeScrollBarVisible(AtkComponentTreeList* tree, bool visible) {
        if (tree is null)
            return;
        var scrollBar = ((AtkComponentList*)tree)->ScrollBarComponent;
        if (scrollBar is null)
            return;
        SetNodeVisible((AtkResNode*)scrollBar->OwnerNode, visible);
    }

    private static void ResetTreeScroll(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        var list = (AtkComponentList*)tree;
        list->FirstVisibleItemIndex = 0;
        list->PendingFirstVisibleItemIndex = 0;
        list->ScrollOffset = 0;
        list->ScrollToItem(0);
        var scrollBar = list->ScrollBarComponent;
        if (scrollBar is not null)
            scrollBar->SetScrollPosition(0);
    }

    private static void ClearTreeDisplay(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        var list = (AtkComponentList*)tree;
        list->SetItemCount(0);
        ResetTreeScroll(tree);
        RefreshTreeListLayout(tree);
    }

    private static void LoadTreeFromAtkSnapshot(
        AtkComponentTreeList* tree,
        AtkValue[] atkSnapshot,
        int slotCount,
        CrystallizeAtkBufferLayout layout,
        AtkComponentTreeList* callbackSource = null) {
        var list = (AtkComponentList*)tree;
        var callback = list->CallBackInterface;
        if (callback is null && callbackSource is not null)
            callback = ((AtkComponentList*)callbackSource)->CallBackInterface;
        if (callback is null || !layout.IsValid)
            return;
        fixed (AtkValue* snapshotPtr = atkSnapshot) {
            tree->LoadAtkValues(
                atkSnapshot.Length,
                snapshotPtr,
                layout.UintValuesOffset,
                layout.StringValuesOffset,
                layout.UintValuesPerItem,
                layout.StringValuesPerItem,
                slotCount,
                callback);
        }
        SyncTreeItemsFromAtk(tree, atkSnapshot, slotCount, layout);
        RefreshTreeListLayout(tree);
    }

    private void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        _displayToSource = [];
        _filteredAtkSlotCount = 0;
        data->CrystallizeItemCount = 0;
        data->CrystallizeItemIndex = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    // rewrite CrystallizeItems[] for filtered display indices (must match ATK u1 after reload)
    private void ProjectVisibleRows(MiragePrismPrismBoxData* data) {
        var visible = _displayToSource.Length;
        var selectedItemId = data->CrystallizeSelectedItem.ItemId;
        for (var displayIndex = 0; displayIndex < visible; displayIndex++) {
            var sourceIndex = _displayToSource[displayIndex];
            if ((uint)sourceIndex >= (uint)_categoryRows.Length)
                continue;
            data->CrystallizeItems[displayIndex] = _categoryRows[sourceIndex];
        }
        data->CrystallizeItemCount = (ushort)visible;
        for (var i = visible; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
        ClampCrystallizeSelection(data, selectedItemId);
    }

    // inverse of ProjectVisibleRows for OnPreRefresh
    private void RestoreFullCategory(MiragePrismPrismBoxData* data) {
        var count = _categoryRows.Length;
        for (var i = 0; i < count; i++)
            data->CrystallizeItems[i] = _categoryRows[i];
        data->CrystallizeItemCount = (ushort)count;
        for (var i = count; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
        _displayToSource = [.. Enumerable.Range(0, count)];
    }

    private static void ClampCrystallizeSelection(MiragePrismPrismBoxData* data, uint previousSelectedItemId) {
        var count = data->CrystallizeItemCount;
        if (count == 0) {
            data->CrystallizeItemIndex = 0;
            return;
        }
        if (previousSelectedItemId != 0) {
            for (ushort i = 0; i < count; i++) {
                if (data->CrystallizeItems[i].ItemId == previousSelectedItemId) {
                    data->CrystallizeItemIndex = i;
                    return;
                }
            }
        }
        if (data->CrystallizeItemIndex >= count)
            data->CrystallizeItemIndex = (ushort)(count - 1);
    }

    // only restore when agent still holds the filtered projection — crystallize updates agent first; stale restore crashes
    private bool ShouldRestoreFullCategoryBeforeNativeRefresh(MiragePrismPrismBoxData* data) {
        if (_needsCategorySnapshot || _categoryRows.Length == 0)
            return false;
        if (MatchesFilteredProjection(data))
            return true;
        if (AgentMatchesCategorySnapshot(data))
            return false;
        MarkCategorySnapshotStale(data, "agent diverged from category snapshot");
        return false;
    }

    private bool MatchesFilteredProjection(MiragePrismPrismBoxData* data) {
        var agentCount = data->CrystallizeItemCount;
        if (_displayToSource.Length == 0 || agentCount != _displayToSource.Length)
            return false;
        for (var i = 0; i < agentCount; i++) {
            var sourceIndex = _displayToSource[i];
            if ((uint)sourceIndex >= (uint)_categoryRows.Length)
                return false;
            if (data->CrystallizeItems[i].ItemId != _categoryRows[sourceIndex].ItemId)
                return false;
        }
        return true;
    }

    private bool AgentMatchesCategorySnapshot(MiragePrismPrismBoxData* data) {
        var agentCount = InferPopulatedCategoryItemCount(data);
        if (agentCount != _categoryRows.Length)
            return false;
        for (var i = 0; i < agentCount; i++) {
            if (data->CrystallizeItems[i].ItemId != _categoryRows[i].ItemId)
                return false;
        }
        return true;
    }

    private void MarkCategorySnapshotStale(MiragePrismPrismBoxData* data, string reason) {
        if (_needsCategorySnapshot)
            return;
        _needsCategorySnapshot = true;
        LogFilterDebug(nameof(MarkCategorySnapshotStale),
            $"{reason} category={data->CrystallizeCategory} agentCount={data->CrystallizeItemCount} snapshot={_categoryRows.Length}");
    }

    private void RequestAddonRefresh(AtkUnitBase* addon) {
        if (_refreshRecursionDepth > 1)
            return;
        addon->OnRefresh(0, null);
    }

    private bool HasValidCategorySnapshot(MiragePrismPrismBoxData* data)
        => data is not null
           && data->CrystallizeCategory == _crystallizeCategory
           && _categoryRows.Length > 0;

    // build _displayToSource from _categoryRows; doesn't touch agent or ATK
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
        LogFilterRebuildSummary();
    }

    private bool ShouldExcludeLeaf(uint itemId)
        => _filters.Any(f => f.IsEnabled && f.ShouldHide(ItemUtil.GetBaseId(itemId).ItemId));

    private bool ShouldExcludeSourceIndex(int sourceIndex)
        => (uint)sourceIndex >= (uint)_categoryRows.Length || ShouldExcludeLeaf(_categoryRows[sourceIndex].ItemId);

    // leaf predicate only — duplicate source indices when CrystallizeFilterFlags on; headers use a separate path
    private Func<int, bool> BuildShouldHideLeafPredicate(HashSet<int> visibleSources) {
        var seenSources = new HashSet<int>();

        return sourceIndex => {
            if (!visibleSources.Contains(sourceIndex))
                return true;
            if (ShouldExcludeSourceIndex(sourceIndex))
                return true;
            if (!seenSources.Add(sourceIndex))
                return true;
            return false;
        };
    }

    private void ParseAtkLayout(bool force = false, int? categoryRowCount = null) {
        if (!HasAtkBufferLayout || _nativeAtkSnapshot.Length == 0) {
            _atkLayout = [];
            _nativeAtkSlotCount = 0;
            return;
        }
        var rowCount = categoryRowCount ?? _categoryRows.Length;
        if (force || _atkLayout.Length == 0) {
            var inferred = CrystallizeListAtk.InferItemCount(_nativeAtkSnapshot, _atkBufferLayout);
            var includeHeaders = rowCount > 0;
            _nativeAtkSlotCount = rowCount > 0
                ? CrystallizeListAtk.InferBoundedItemCount(_nativeAtkSnapshot, inferred, rowCount, includeHeaders, _atkBufferLayout)
                : inferred;
            _atkLayout = CrystallizeListAtk.Parse(_nativeAtkSnapshot, _nativeAtkSlotCount, _atkBufferLayout, rowCount);
        }
    }

    // agent has full category but tree still shows filtered row count
    private bool IsNativeTreeTruncatedVersusSnapshot(MiragePrismPrismBoxData* data) {
        if (_categoryRows.Length <= 0)
            return false;
        var listLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : 0;
        if (listLength <= 0)
            return false;
        var agentCount = InferPopulatedCategoryItemCount(data);
        return agentCount >= _categoryRows.Length && listLength < agentCount;
    }

    private int GetLayoutMaxSourceIndex() {
        var maxSource = -1;
        for (var i = 0; i < _atkLayout.Length; i++) {
            if (!_atkLayout[i].IsLeaf)
                continue;
            if (_atkLayout[i].SourceIndex > maxSource)
                maxSource = _atkLayout[i].SourceIndex;
        }
        return maxSource >= 0 ? maxSource + 1 : 0;
    }

    // rebuild _categoryRows from agent + native ATK after refresh
    private bool TryCaptureCategorySnapshotAfterNative(MiragePrismPrismBoxData* data) {
        if (_nativeAtkSnapshot.Length == 0)
            return false;

        ParseAtkLayout(force: true, categoryRowCount: 0); // uncapped parse so layoutMax sees all leaf u1 values
        if (_atkLayout.Length == 0)
            return false;

        var layoutMaxSource = GetLayoutMaxSourceIndex();
        var scannedCount = InferPopulatedCategoryItemCount(data);
        var listLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : 0;
        var rowCount = Math.Max(scannedCount, listLength);
        if (_crystallizeFilterFlagsSnapshot == 0) // gearset off: trust layoutMax; on: layoutMax is often stale
            rowCount = Math.Max(rowCount, layoutMaxSource);
        if (rowCount <= 0)
            return false;

        _categoryRows = new PrismBoxCrystallizeItem[rowCount];
        var populatedFromAgent = Math.Min(scannedCount, rowCount);
        for (var i = 0; i < populatedFromAgent; i++)
            _categoryRows[i] = data->CrystallizeItems[i];

        FillMissingCategoryRowsFromNative(data);
        _nativeCategoryItemCount = rowCount;
        _needsCategorySnapshot = false;
        _crystallizeCategory = data->CrystallizeCategory;
        LogFilterDebug(nameof(TryCaptureCategorySnapshotAfterNative),
            $"category={_crystallizeCategory} agentRows={rowCount} scanned={scannedCount} layoutMax={layoutMaxSource} listLength={listLength} filterFlags={data->CrystallizeFilterFlags}");
        ParseAtkLayout(force: true);
        return _categoryRows.Any(row => row.ItemId != 0);
    }

    // fill rows the game only materialized in ATK/tree (common after gearset toggles)
    private void FillMissingCategoryRowsFromNative(MiragePrismPrismBoxData* data) {
        for (var slot = 0; slot < _atkLayout.Length; slot++) {
            ref readonly var entry = ref _atkLayout[slot];
            if (!entry.IsLeaf)
                continue;
            var sourceIndex = entry.SourceIndex;
            if (sourceIndex < 0 || sourceIndex >= _categoryRows.Length)
                continue;
            if (_categoryRows[sourceIndex].ItemId != 0)
                continue;
            if (data->CrystallizeItems[sourceIndex].ItemId != 0) {
                _categoryRows[sourceIndex] = data->CrystallizeItems[sourceIndex];
                continue;
            }
            if (CrystallizeListAtk.TryReadCategoryRow(_nativeAtkSnapshot, slot, entry, _atkBufferLayout, out var row))
                _categoryRows[sourceIndex] = row;
            else if (_nativeTreeList is not null
                     && CrystallizeListAtk.TryReadCategoryRowFromTreeItem(_nativeTreeList->GetItem(slot), entry, out row))
                _categoryRows[sourceIndex] = row;
        }
    }

    private void CaptureNativeAtkSnapshot(AtkUnitBase* addon) {
        if (addon->AtkValues is null || addon->AtkValuesCount <= 0) {
            _nativeAtkSnapshot = [];
            return;
        }
        var copy = new AtkValue[addon->AtkValuesCount];
        for (var i = 0; i < addon->AtkValuesCount; i++)
            copy[i] = addon->AtkValues[i];
        _nativeAtkSnapshot = copy;
        LogNativeListMetrics(addon);
    }

    // clone ATK, ApplyToBuffer, LoadAtkValues into node 11
    private void RepopulateDisplayTreeList(AtkUnitBase* addon) {
        var tree = _nativeTreeList;
        if (tree is null || !HasAtkBufferLayout)
            return;
        if (_nativeAtkSnapshot.Length == 0 || _atkLayout.Length == 0) {
            ClearTreeDisplay(tree);
            return;
        }
        var workingAtk = CrystallizeListAtk.Clone(_nativeAtkSnapshot);
        if (IsFilteringActive) {
            var visibleSources = new HashSet<int>(_displayToSource);
            _filteredAtkSlotCount = CrystallizeListAtk.ApplyToBuffer(
                workingAtk,
                _atkLayout,
                BuildShouldHideLeafPredicate(visibleSources),
                _atkBufferLayout,
                _nativeAtkSlotCount,
                IncludeSectionHeaders,
                visibleSources,
                ShouldExcludeSourceIndex);
        }
        if (_filteredAtkSlotCount <= 0) {
            var clearedAtk = CrystallizeListAtk.Clone(_nativeAtkSnapshot);
            var clearThrough = Math.Max(_nativeAtkSlotCount, 1);
            CrystallizeListAtk.ClearSlots(clearedAtk, 0, clearThrough, _atkBufferLayout);
            LoadTreeFromAtkSnapshot(tree, clearedAtk, 0, _atkBufferLayout, _nativeTreeList);
            ResetTreeScroll(tree);
            LogFilterDebug(nameof(RepopulateDisplayTreeList),
                $"cleared native tree (no filtered slots) clearedSlots={clearThrough} items.Count={tree->Items.Count}");
            return;
        }
        LoadTreeFromAtkSnapshot(tree, workingAtk, _filteredAtkSlotCount, _atkBufferLayout, _nativeTreeList);
        ResetTreeScroll(tree);
        RefreshTreeListLayout(tree);
        var list = (AtkComponentList*)tree;
        LogFilterDebug(nameof(RepopulateDisplayTreeList),
            $"loaded native filtered slots={_filteredAtkSlotCount} items.Count={tree->Items.Count} listLength={list->ListLength} numVisible={list->NumVisibleItems} itemHeight={list->ItemHeight} listHeight={list->ListHeight} hasRenderer={(nint)list->FirstAtkComponentListItemRenderer != 0} hasScrollBar={(nint)list->ScrollBarComponent != 0}");
        _ = addon;
    }

    private static void SyncTreeItemsFromAtk(AtkComponentTreeList* tree, AtkValue[] atkValues, int slotCount, CrystallizeAtkBufferLayout layout) {
        if (slotCount <= 0)
            return;
        var itemCount = Math.Min(slotCount, tree->Items.Count);
        for (var slot = 0; slot < itemCount; slot++) {
            var item = tree->GetItem(slot);
            if (item is null)
                continue;
            CrystallizeListAtk.CopySlotToTreeItem(atkValues, slot, item, layout);
        }
    }

    private void LoadAtkValuesDetour(
        AtkComponentTreeList* thisPtr,
        int atkValuesCount,
        AtkValue* atkValues,
        int uintValuesOffset,
        int stringValuesOffset,
        int uintValuesCountPerItem,
        int stringValuesCountPerItem,
        int itemCount,
        ListComponentCallBackInterface* callBackInterface) {
        if (itemCount > 0)
            TryCaptureAtkBufferLayout(thisPtr, uintValuesOffset, stringValuesOffset, uintValuesCountPerItem, stringValuesCountPerItem, atkValuesCount);

        _loadAtkValuesHook!.Original(
            thisPtr,
            atkValuesCount,
            atkValues,
            uintValuesOffset,
            stringValuesOffset,
            uintValuesCountPerItem,
            stringValuesCountPerItem,
            itemCount,
            callBackInterface);
    }

    private bool TryCaptureAtkBufferLayout(
        AtkComponentTreeList* tree,
        int uintValuesOffset,
        int stringValuesOffset,
        int uintValuesPerItem,
        int stringValuesPerItem,
        int atkValuesCount) {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName);
        if (addon is null)
            return false;

        var node = addon->GetNodeById(ItemTreeListNodeId);
        if (node is null)
            return false;

        var nativeTree = node->GetAsAtkComponentTreeList();
        if (nativeTree != tree)
            return false;

        var candidate = new CrystallizeAtkBufferLayout {
            UintValuesOffset = uintValuesOffset,
            StringValuesOffset = stringValuesOffset,
            UintValuesPerItem = uintValuesPerItem,
            StringValuesPerItem = stringValuesPerItem,
        };
        if (!candidate.IsValid)
            return false;

        if (atkValuesCount > 0
            && stringValuesOffset + stringValuesPerItem > atkValuesCount)
            return false;

        if (_atkBufferLayout.Matches(candidate))
            return true;

        _atkBufferLayout = candidate;
        LogFilterDebug(nameof(TryCaptureAtkBufferLayout),
            $"captured uintOffset={candidate.UintValuesOffset} stringOffset={candidate.StringValuesOffset} uintPerItem={candidate.UintValuesPerItem} stringPerItem={candidate.StringValuesPerItem} atkValuesCount={atkValuesCount}");

        return true;
    }

    private static void RefreshTreeListLayout(AtkComponentTreeList* tree) {
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
        if (data->CrystallizeCategory == _crystallizeCategory && _categoryRows.Length > 0)
            return;
        if (data->CrystallizeCategory != _crystallizeCategory) {
            _crystallizeCategory = data->CrystallizeCategory;
            _categoryRows = [];
            _nativeCategoryItemCount = 0;
            InvalidateNativeAtkCache();
            ClearFilterLogSignatures();
            LogFilterDebug(nameof(EnsureCategoryTracked), $"category -> {_crystallizeCategory}");
        }
        if (_categoryRows.Length > 0)
            return;
        _nativeCategoryItemCount = InferCategoryItemCount(data);
        if (_nativeCategoryItemCount > 0)
            _needsCategorySnapshot = true;
    }

    private void InvalidateNativeAtkCache() {
        _nativeAtkSnapshot = [];
        _atkLayout = [];
        _nativeAtkSlotCount = 0;
        _filteredAtkSlotCount = 0;
    }

    // fallback before PostRefresh; prefer TryCaptureCategorySnapshotAfterNative when ATK is available
    private void CaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        var agentCount = _nativeCategoryItemCount > 0 ? _nativeCategoryItemCount : InferCategoryItemCount(data);
        if (agentCount <= 0) {
            _categoryRows = [];
            _displayToSource = [];
            _needsCategorySnapshot = false;
            return;
        }
        // don't overwrite good snapshot with truncated agent read
        if (IsFilteringActive && _categoryRows.Length > 0 && agentCount < _categoryRows.Length && !_needsCategorySnapshot) {
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
        SyncCrystallizeFilterFlagsSnapshot(data);
        LogFilterDebug(nameof(CaptureCategorySnapshot),
            $"category={_crystallizeCategory} agentRows={agentCount} filterFlags={data->CrystallizeFilterFlags}");
    }

    private void SyncCrystallizeFilterFlagsSnapshot(MiragePrismPrismBoxData* data) {
        _crystallizeFilterFlagsSnapshot = data->CrystallizeFilterFlags;
    }

    private bool TryDetectCrystallizeFilterFlagsChange(MiragePrismPrismBoxData* data, string phase) {
        if (_crystallizeFilterFlagsSnapshot == byte.MaxValue) {
            SyncCrystallizeFilterFlagsSnapshot(data);
            return false;
        }
        if (data->CrystallizeFilterFlags == _crystallizeFilterFlagsSnapshot)
            return false;
        var previous = _crystallizeFilterFlagsSnapshot;
        SyncCrystallizeFilterFlagsSnapshot(data);
        _crystallizeFilterFlagsChangedThisRefresh = true;
        LogFilterDebug(phase, $"CrystallizeFilterFlags changed {previous} -> {data->CrystallizeFilterFlags}");
        return true;
    }

    private bool TryHandleCrystallizeFilterFlagsChange(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!TryDetectCrystallizeFilterFlagsChange(data, nameof(OnAddonUpdate)))
            return false;
        ApplyFilterAfterFlagsChange(addon, data);
        return true;
    }

    // gearset filter toggled mid-frame — recapture and refilter without waiting for refresh
    private void ApplyFilterAfterFlagsChange(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!IsAddonUsable(addon) || !IsFilteringActive)
            return;

        ResolveNativeTree(addon);
        EnsureNativeTreeVisible(addon);
        _needsCategorySnapshot = true;
        _nativeCategoryItemCount = 0;
        CaptureNativeAtkSnapshot(addon);
        if (!TryCaptureCategorySnapshotAfterNative(data)) {
            LogFilterDebug(nameof(ApplyFilterAfterFlagsChange), "aborted (category snapshot unavailable)");
            RequestAddonRefresh(addon);
            return;
        }
        if (_nativeAtkSnapshot.Length == 0 || _atkLayout.Length == 0) {
            LogFilterDebug(nameof(ApplyFilterAfterFlagsChange), "aborted (native capture unavailable)");
            return;
        }

        ApplyFilterAfterNativeRefresh(addon, data);
        LogFilterDebug(nameof(ApplyFilterAfterFlagsChange),
            $"refiltered after flags change nativeAtkSlots={_nativeAtkSlotCount} filteredAtkSlots={_filteredAtkSlotCount} snapshot={_categoryRows.Length} filterFlags={_crystallizeFilterFlagsSnapshot}");
    }

    private static MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }

    private static int InferCategoryItemCount(MiragePrismPrismBoxData* data) {
        var populated = InferPopulatedCategoryItemCount(data);
        if (populated > 0)
            return populated;
        return data->CrystallizeItemCount > 0 ? data->CrystallizeItemCount : 0;
    }

    // CrystallizeItemCount is unreliable while filtering
    private static int InferPopulatedCategoryItemCount(MiragePrismPrismBoxData* data) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }
        return lastIndex >= 0 ? lastIndex + 1 : 0;
    }

    // CrystallizeFilterFlags / PrismBoxItemIds can change outside refresh
    private void OnAddonUpdate(AtkUnitBase* addon) {
        if (!IsAddonUsable(addon) || !IsFilteringActive)
            return;

        var data = GetData();
        if (data is null)
            return;

        if (TryHandleCrystallizeFilterFlagsChange(addon, data))
            return;

        var mirage = MirageManager.Instance();
        if (mirage is null || !TryDetectPrismBoxItemIdsChange(mirage))
            return;
        MarkCategorySnapshotStale(data, "PrismBoxItemIds changed");
        InvalidateNativeAtkCache();
        ClearFilterLogSignatures();
        RequestAddonRefresh(addon);
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
        _nativeAtkSnapshot = [];
        _atkLayout = [];
        _nativeAtkSlotCount = 0;
        _filteredAtkSlotCount = 0;
        _nativeCategoryItemCount = 0;
        _crystallizeCategory = int.MinValue;
        _needsCategorySnapshot = false;
        _nativeTreeList = null;
        _nativeTreeResNode = null;
        _prismBoxItemIdsInitialized = false;
        _crystallizeFilterFlagsSnapshot = byte.MaxValue;
        ClearFilterLogSignatures();
    }

    private static bool IsAddonUsable(AtkUnitBase* addon)
        => addon is not null && addon->IsVisible;
}
