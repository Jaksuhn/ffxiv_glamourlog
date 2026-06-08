using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using System.Text;

namespace GlamourLog.Features.PrismBox;

// GlamourLog row filters for the Prism Box crystallize picker (MiragePrismPrismBoxCrystallize).
//
// The game builds the list from AgentMiragePrismPrismBox data and renders it on native tree node 11.
// We cannot replace that UI wholesale: row callbacks, scroll behaviour, and the in-game gearset filter
// (CrystallizeFilterFlags on the agent) all assume that tree and those agent indices stay wired up.
//
// So we keep a pristine category snapshot (_categoryRows), decide which source indices survive our filters
// (_displayToSource), rewrite the agent buffer to only those rows (ProjectVisibleRows), then compact the
// native ATK slot buffer and reload node 11 in place (RepopulateDisplayTreeList + CrystallizeListAtk).
//
// Refresh is hook-driven (KTK AddonController):
//   OnPreRefresh  — restore full category only when the agent still holds our filtered projection;
//                   skip restore after crystallize / PrismBoxItemIds changes so we never resurrect removed rows
//   native refresh — game rebuilds ATK + tree from agent (respects CrystallizeFilterFlags)
//   OnPostRefresh — capture that native output, optionally refresh _categoryRows, apply our filters
//
// We do not drive a separate duplicate tree for display anymore. Duplicate-tree helpers remain for
// callback wiring experiments, but SetFilteredTreeActive always leaves node 11 visible and filtered.
internal sealed unsafe class CrystallizeListHandler : IDisposable {
    private const string AddonName = "MiragePrismPrismBoxCrystallize";
    private const int MaxCategoryItems = 140;
    private const uint ItemTreeListNodeId = 11;
    private const uint FilteredTreeNodeIdOffset = 10_000;
    private const int MaxHiddenItemLogLines = 48;
    private const int PrismBoxItemIdCount = 800;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly AddonController<AtkUnitBase> _addonController;
    private readonly uint[] _prismBoxItemIdsSnapshot = new uint[PrismBoxItemIdCount];

    // --- category snapshot (source of truth for filter decisions) ---
    // Indices in _categoryRows match agent CrystallizeItems[] slots for the current category tab.
    // _displayToSource lists which of those indices should appear after GlamourLog filters run.

    // --- native ATK mirror (source of truth for tree layout) ---
    // Parsed layout (_atkLayout) maps each ATK slot to header vs leaf and leaf -> source index.
    // Filtering compacts slots in a clone, then LoadTreeFromAtkSnapshot pushes into node 11.

    // --- in-game gearset filter (CrystallizeFilterFlags) ---
    // Tracked separately from our filters. When it toggles, row count and ATK layout change;
    // we recapture _categoryRows from native output instead of restoring the old snapshot first.

    // --- duplicate tree (legacy; not used for final display) ---

    private bool _prismBoxItemIdsInitialized; // first MirageManager.PrismBoxItemIds baseline captured
    private byte _crystallizeFilterFlagsSnapshot = byte.MaxValue; // last seen agent CrystallizeFilterFlags (gearset filter)
    private string? _lastFilterSummarySignature; // log dedupe: RebuildFilterMap summary
    private string? _lastPreFilterSignature; // log dedupe: full snapshot item list
    private string? _lastPostFilterSignature; // log dedupe: visible item list
    private string? _lastHiddenItemsSignature; // log dedupe: per-row hide reasons
    private string? _lastApplyPipelineSignature; // log dedupe: post-apply slot counts
    private string? _lastFilterOffStateSignature; // log dedupe: filters-disabled tree state
    private string? _lastFilterOnStateSignature; // log dedupe: filters-enabled tree state
    private PrismBoxCrystallizeItem[] _categoryRows = []; // full tab snapshot; index = agent source slot
    private int[] _displayToSource = []; // filtered source indices that should appear
    private int _nativeCategoryItemCount; // row count inferred at category track time
    private int _crystallizeCategory = int.MinValue; // agent CrystallizeCategory tab id
    private bool _needsCategorySnapshot; // recapture _categoryRows on next PostRefresh
    private bool _crystallizeFilterFlagsChangedThisRefresh; // gearset filter toggled this refresh cycle
    private int _refreshRecursionDepth; // guard against re-entrant OnRefresh during filter repair
    private AtkValue[] _nativeAtkSnapshot = []; // copy of addon ATK buffer after native refresh
    private CrystallizeAtkSlot[] _atkLayout = []; // parsed header/leaf + source index per ATK slot
    private int _nativeAtkSlotCount; // populated slot count in _nativeAtkSnapshot
    private int _filteredAtkSlotCount; // slot count after ApplyToBuffer compaction
    private short _nativeListItemHeight; // cached row height from native list (for duplicate tree sync)
    private short _nativeListHeight; // cached viewport height from native list
    private AtkResNode* _nativeTreeResNode; // node 11 — the tree we filter in place
    private AtkComponentTreeList* _nativeTreeList;
    private AtkResNode* _filteredTreeResNode; // duplicated node (legacy; not shown)
    private AtkComponentTreeList* _filteredTreeList;
    private bool _filteredTreeReady; // duplicate tree node exists and is wired
    private bool _filteredTreeDisplayed; // unused; display always stays on native
    private uint _duplicateTreeNodeId; // cached node id for duplicate tree lookup

    public CrystallizeListHandler() {
        _filters = [
            new HideDresserDepositedFilter(),
            new HideArmoireEligibleFilter(),
            new HideNonOutfitItemsFilter(),
        ];
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
        ClearFilterState();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool IncludeSectionHeaders => _displayToSource.Length > 0;

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(ApplyConfigChange);
    }

    // User toggled a GlamourLog filter in settings. Always restore the full agent category and invalidate
    // cached ATK so the next refresh rebuilds from an unfiltered native tree — applying on a truncated
    // filtered tree leaves listLength << snapshot and breaks toggles / dresser updates.
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
        if (!HasValidCategorySnapshot(data) && !TryCaptureCategorySnapshot(data)) {
            _needsCategorySnapshot = true;
            LogFilterDebug(nameof(ApplyConfigChange),
                $"filters enabled, awaiting category snapshot category={data->CrystallizeCategory} snapshot={_categoryRows.Length}");
            addon->OnRefresh(0, null);
            return;
        }
        if (_categoryRows.Length > 0)
            RestoreFullCategory(data);
        InvalidateNativeAtkCache();
        _needsCategorySnapshot = _categoryRows.Length > 0;
        ClearFilterLogSignatures();
        LogFilterDebug(nameof(ApplyConfigChange),
            $"filters enabled category={_crystallizeCategory} snapshot={_categoryRows.Length} needsSnapshot={_needsCategorySnapshot}");
        addon->OnRefresh(0, null);
    }

    private void OnSetup(AtkUnitBase* addon) {
        ReleaseFilteredTree(addon);
        SetFilteredTreeActive(addon, active: false);
    }

    // Runs before the game's crystallize addon refresh. Our filtered state shrinks CrystallizeItemCount;
    // unless we put the full snapshot back now, native refresh rebuilds a truncated list and ATK capture
    // will be wrong on the next filter toggle.
    private void OnPreRefresh(AtkUnitBase* addon) {
        var data = GetData();
        if (data is null)
            return;

        _crystallizeFilterFlagsChangedThisRefresh = false;
        if (IsFilteringActive) {
            SetFilteredTreeActive(addon, active: false);
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

        // CrystallizeFilterFlags just changed (in-game gearset filter). The agent already holds the new
        // row set; restoring our old snapshot would fight native and desync ATK source indices.
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

    // Runs after native refresh. Capture what the game built, then optionally replace it with our filter.
    private void OnPostRefresh(AtkUnitBase* addon) {
        if (!IsAddonUsable(addon))
            return;

        var data = GetData();
        if (data is null)
            return;

        _refreshRecursionDepth++;
        try {
            OnPostRefreshCore(addon, data);
        } finally {
            _refreshRecursionDepth--;
        }
    }

    private void OnPostRefreshCore(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!IsFilteringActive) {
            ResolveNativeTree(addon);
            if (_nativeTreeList is not null)
                RestoreNativeTreeFromSnapshot(addon);
            SetFilteredTreeActive(addon, active: false);
            LogFilterOffState(nameof(OnPostRefresh), addon, data);
            return;
        }

        ResolveNativeTree(addon);
        EnsureCategoryTracked(data);
        TryDetectCrystallizeFilterFlagsChange(data, nameof(OnPostRefresh));
        SetFilteredTreeActive(addon, active: false);
        CaptureNativeAtkSnapshot(addon);
        if (_needsCategorySnapshot || _crystallizeFilterFlagsChangedThisRefresh) {
            if (!TryCaptureCategorySnapshotAfterNative(data)) {
                LogFilterDebug(nameof(OnPostRefresh), "filter enable aborted (category snapshot unavailable after native refresh)");
                return;
            }
        } else {
            ParseAtkLayout(force: true);
        }
        if (_nativeAtkSnapshot.Length == 0 || _atkLayout.Length == 0 || _categoryRows.Length == 0) {
            LogFilterDebug(nameof(OnPostRefresh),
                $"filter enable aborted (nativeAtk={_nativeAtkSnapshot.Length} layout={_atkLayout.Length} snapshot={_categoryRows.Length})");
            return;
        }
        if (IsNativeTreeTruncatedVersusSnapshot(data)) {
            RestoreFullCategory(data);
            var listLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : (short)0;
            LogFilterDebug(nameof(OnPostRefresh),
                $"native tree truncated vs snapshot (listLength={listLength} snapshot={_categoryRows.Length}) — requesting refresh");
            RequestAddonRefresh(addon);
            return;
        }
        ApplyFilterAfterNativeRefresh(addon, data);
        LogFilterOnState(nameof(OnPostRefresh), addon, data);
    }

    private void PrepareFilteredTreeForPopulation() {
        PrepareDuplicateTreeCallbacks();
    }

    // Core apply path: map -> agent projection -> ATK compact -> reload native tree.
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

        var tree = _nativeTreeList;
        var itemsCount = tree->Items.Count;
        var listLength = ((AtkComponentList*)tree)->ListLength;
        var getItemCount = ((AtkComponentList*)tree)->GetItemCount();
        var applySummary =
            $"leaves={_displayToSource.Length} nativeAtkSlots={_nativeAtkSlotCount} filteredAtkSlots={_filteredAtkSlotCount} items.Count={itemsCount} listLength={listLength} getItemCount={getItemCount} agentCount={data->CrystallizeItemCount} filterFlags={_crystallizeFilterFlagsSnapshot} flagsChangedThisRefresh={_crystallizeFilterFlagsChangedThisRefresh}";
        if (applySummary != _lastApplyPipelineSignature) {
            _lastApplyPipelineSignature = applySummary;
            LogFilterDebug(nameof(ApplyFilterAfterNativeRefresh), $"applied {applySummary}");
        }
    }

    private void OnFinalize(AtkUnitBase* addon) {
        if (addon is not null) {
            SetFilteredTreeActive(addon, active: false);
            var data = GetData();
            if (data is not null && _categoryRows.Length > 0)
                RestoreFullCategory(data);
        }
        ReleaseFilteredTree(addon);
        ClearFilterState();
    }

    // --- duplicate tree infrastructure (not used for display; kept for callback / layout experiments) ---

    private bool EnsureFilteredTreeList(AtkUnitBase* addon) {
        ResolveNativeTree(addon);
        if (_nativeTreeList is null || _nativeTreeResNode is null) {
            LogFilterDebug(nameof(EnsureFilteredTreeList), "aborted (native tree missing)");
            return false;
        }
        if (_filteredTreeReady && IsCachedFilteredTreeValid())
            return true;
        if (_filteredTreeReady && TryResolveFilteredTree(addon, null))
            return true;
        if (_filteredTreeReady) {
            LogFilterDebug(nameof(EnsureFilteredTreeList), "duplicate missing after ready flag, recreating");
            _filteredTreeReady = false;
        }
        if (addon->UldManager.LoadedState != AtkLoadState.Loaded) {
            LogFilterDebug(nameof(EnsureFilteredTreeList), "aborted (uld not loaded)");
            return false;
        }
        AtkResNode* createdNode = null;
        if (!TryResolveFilteredTree(addon, null)) {
            var existing = FindExistingDuplicateTreeNode(addon);
            if (existing is not null && TryAssignFilteredTreeNode(existing)) {
                LogFilterDebug(nameof(EnsureFilteredTreeList),
                    $"reused existing duplicate nodeId={existing->NodeId}");
            }
            else {
                createdNode = addon->UldManager.DuplicateComponentNode(ItemTreeListNodeId, 1, FilteredTreeNodeIdOffset);
                addon->UldManager.UpdateDrawNodeList();
                ResolveNativeTree(addon);
                if (!TryResolveFilteredTree(addon, createdNode)) {
                    LogFilterDebug(nameof(EnsureFilteredTreeList),
                        "aborted (duplicate node missing after DuplicateComponentNode)");
                    return false;
                }
            }
        }
        if (_nativeTreeResNode != _filteredTreeResNode) {
            LinkDuplicateOverNative(_nativeTreeResNode, _filteredTreeResNode);
            PrepareDuplicateTreeCallbacks();
        }
        _filteredTreeReady = true;
        if (_filteredTreeResNode is not null)
            _duplicateTreeNodeId = _filteredTreeResNode->NodeId;
        var duplicateBaseId = _filteredTreeResNode is not null ? _filteredTreeResNode->GetBaseNodeId() : 0u;
        LogFilterDebug(nameof(EnsureFilteredTreeList),
            $"duplicate ready nativeNodeId={_nativeTreeResNode->NodeId} duplicateNodeId={_filteredTreeResNode->NodeId} duplicateBaseId={duplicateBaseId} sameNode={_nativeTreeResNode == _filteredTreeResNode}");
        return _filteredTreeResNode != _nativeTreeResNode;
    }

    private void PrepareDuplicateTreeCallbacks() {
        if (_filteredTreeList is null || _nativeTreeList is null || _filteredTreeList == _nativeTreeList)
            return;
        var filteredList = (AtkComponentList*)_filteredTreeList;
        var nativeList = (AtkComponentList*)_nativeTreeList;
        if (filteredList->CallBackInterface is null && nativeList->CallBackInterface is not null)
            filteredList->CallBackInterface = nativeList->CallBackInterface;
    }

    private bool IsCachedFilteredTreeValid() {
        if (_filteredTreeResNode is null || _filteredTreeList is null || _nativeTreeResNode is null)
            return false;
        if (_filteredTreeResNode == _nativeTreeResNode)
            return false;
        return _filteredTreeResNode->GetAsAtkComponentTreeList() is not null;
    }

    private void InvalidateTreeNodeCache() {
        _nativeTreeResNode = null;
        _nativeTreeList = null;
        _filteredTreeResNode = null;
        _filteredTreeList = null;
    }

    private void RefreshFilteredTreePointers(AtkUnitBase* addon) {
        if (IsCachedFilteredTreeValid())
            return;
        ResolveNativeTree(addon);
        if (_duplicateTreeNodeId != 0) {
            var byId = addon->UldManager.SearchNodeById(_duplicateTreeNodeId);
            if (TryAssignFilteredTreeNode(byId))
                return;
        }
        var existing = FindExistingDuplicateTreeNode(addon);
        if (TryAssignFilteredTreeNode(existing))
            return;
        TryResolveFilteredTree(addon, null);
    }

    private bool TryAssignFilteredTreeNode(AtkResNode* node) {
        if (node is null || node == _nativeTreeResNode)
            return false;
        var tree = node->GetAsAtkComponentTreeList();
        if (tree is null)
            return false;
        _filteredTreeResNode = node;
        _filteredTreeList = tree;
        return true;
    }

    private void ResolveNativeTree(AtkUnitBase* addon) {
        var nativeNode = addon->GetNodeById(ItemTreeListNodeId);
        if (nativeNode is null)
            return;
        _nativeTreeResNode = nativeNode;
        _nativeTreeList = nativeNode->GetAsAtkComponentTreeList();
    }

    private bool TryResolveFilteredTree(AtkUnitBase* addon, AtkResNode* createdNode) {
        if (IsCachedFilteredTreeValid())
            return true;

        ResolveNativeTree(addon);
        if (_nativeTreeResNode is null)
            return false;

        if (TryAssignFilteredTreeNode(createdNode))
            return true;

        if (_duplicateTreeNodeId != 0) {
            var byCachedId = addon->UldManager.SearchNodeById(_duplicateTreeNodeId);
            if (TryAssignFilteredTreeNode(byCachedId))
                return true;
        }

        var duplicated = addon->UldManager.GetDuplicatedNode(ItemTreeListNodeId, 0, FilteredTreeNodeIdOffset);
        if (TryAssignFilteredTreeNode(duplicated))
            return true;

        var byOffsetId = addon->UldManager.SearchNodeById(ItemTreeListNodeId + FilteredTreeNodeIdOffset);
        if (TryAssignFilteredTreeNode(byOffsetId))
            return true;

        duplicated = addon->UldManager.GetDuplicatedNode(ItemTreeListNodeId, 0, 1);
        if (TryAssignFilteredTreeNode(duplicated))
            return true;

        var existing = FindExistingDuplicateTreeNode(addon);
        if (TryAssignFilteredTreeNode(existing))
            return true;

        return IsCachedFilteredTreeValid();
    }

    private AtkResNode* FindExistingDuplicateTreeNode(AtkUnitBase* addon) {
        ResolveNativeTree(addon);
        if (_nativeTreeResNode is null)
            return null;

        if (_duplicateTreeNodeId != 0) {
            var byId = addon->UldManager.SearchNodeById(_duplicateTreeNodeId);
            if (byId is not null && byId != _nativeTreeResNode && byId->GetAsAtkComponentTreeList() is not null)
                return byId;
        }

        AtkResNode* best = null;
        foreach (var nodePtr in addon->UldManager.Nodes) {
            var node = nodePtr.Value;
            if (node is null || node == _nativeTreeResNode || !node->IsDuplicatedNode())
                continue;
            if (node->GetAsAtkComponentTreeList() is null)
                continue;
            if (best is null || node->NodeId > best->NodeId)
                best = node;
        }
        return best;
    }

    private bool UsesDistinctDuplicateTree
        => _filteredTreeList is not null && _nativeTreeList is not null && _filteredTreeList != _nativeTreeList;

    private static void LinkDuplicateOverNative(AtkResNode* nativeNode, AtkResNode* duplicateNode) {
        duplicateNode->X = nativeNode->X;
        duplicateNode->Y = nativeNode->Y;
        duplicateNode->ScaleX = nativeNode->ScaleX;
        duplicateNode->ScaleY = nativeNode->ScaleY;
        duplicateNode->Width = nativeNode->Width;
        duplicateNode->Height = nativeNode->Height;
        duplicateNode->OriginX = nativeNode->OriginX;
        duplicateNode->OriginY = nativeNode->OriginY;
        duplicateNode->SetPriority((ushort)(nativeNode->GetPriority() + 1));
        if (duplicateNode->ParentNode != nativeNode->ParentNode && nativeNode->ParentNode is not null) {
            duplicateNode->ParentNode = nativeNode->ParentNode;
            duplicateNode->PrevSiblingNode = nativeNode;
            duplicateNode->NextSiblingNode = nativeNode->NextSiblingNode;
            if (nativeNode->NextSiblingNode is not null)
                nativeNode->NextSiblingNode->PrevSiblingNode = duplicateNode;
            nativeNode->NextSiblingNode = duplicateNode;
        }
    }

    private void SyncFilteredTreePlacement(AtkUnitBase* addon) {
        if (_nativeTreeResNode is null || _filteredTreeResNode is null)
            return;
        LinkDuplicateOverNative(_nativeTreeResNode, _filteredTreeResNode);
        SyncFilteredTreeListMetrics();
        addon->UldManager.UpdateDrawNodeList();
    }

    private void SyncFilteredTreeListMetrics() {
        if (_filteredTreeList is null || _nativeTreeList is null)
            return;
        var filteredList = (AtkComponentList*)_filteredTreeList;
        var nativeList = (AtkComponentList*)_nativeTreeList;
        filteredList->ListWidth = nativeList->ListWidth;
        filteredList->ListHeight = nativeList->ListHeight;
        filteredList->ItemWidth = nativeList->ItemWidth;
        filteredList->ItemHeight = nativeList->ItemHeight;
        filteredList->RowStepY = nativeList->RowStepY;
        filteredList->VisibleRowCount = nativeList->VisibleRowCount;
        filteredList->NumVisibleRows = nativeList->NumVisibleRows;
        filteredList->NumVisibleColumns = nativeList->NumVisibleColumns;
        filteredList->IsVerticalScroll = nativeList->IsVerticalScroll;
        filteredList->IsScrollBarEnabled = nativeList->IsScrollBarEnabled;
        filteredList->IsScrollBarVisible = nativeList->IsScrollBarVisible;
        if (filteredList->ListHeight <= 0 && _filteredTreeResNode is not null)
            filteredList->ListHeight = (short)_filteredTreeResNode->Height;
        if (filteredList->ItemHeight <= 1 && nativeList->ItemHeight > 1)
            filteredList->ItemHeight = nativeList->ItemHeight;
        else if (filteredList->ItemHeight <= 1 && nativeList->RowStepY > 1)
            filteredList->ItemHeight = nativeList->RowStepY;
        else if (filteredList->ItemHeight <= 1 && _nativeListItemHeight > 1)
            filteredList->ItemHeight = _nativeListItemHeight;
        if (filteredList->RowStepY <= 1 && nativeList->RowStepY > 1)
            filteredList->RowStepY = nativeList->RowStepY;
        if (filteredList->ListHeight <= 0 && _nativeListHeight > 0)
            filteredList->ListHeight = _nativeListHeight;
        if (filteredList->ItemHeight <= 1 && filteredList->RowStepY > 1)
            filteredList->ItemHeight = filteredList->RowStepY;
        if (filteredList->NumVisibleRows <= 0 && filteredList->ItemHeight > 1 && filteredList->ListHeight > 0)
            filteredList->NumVisibleRows = (short)(filteredList->ListHeight / filteredList->ItemHeight);
        if (filteredList->NumVisibleItems <= 0 && filteredList->NumVisibleRows > 0)
            filteredList->NumVisibleItems = (short)(filteredList->NumVisibleColumns > 0
                ? filteredList->NumVisibleColumns * filteredList->NumVisibleRows
                : filteredList->NumVisibleRows);
    }

    // Always show native node 11. The active parameter is ignored — we filter in place on the native tree.
    private void SetFilteredTreeActive(AtkUnitBase* addon, bool active) {
        ResolveNativeTree(addon);
        _filteredTreeDisplayed = false;
        ApplyItemTreeVisibility(addon, _nativeTreeResNode);
        _ = active;
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

    private void RestoreNativeTreeFromSnapshot(AtkUnitBase* addon) {
        var tree = _nativeTreeList;
        if (tree is null || _nativeAtkSnapshot.Length == 0 || _nativeAtkSlotCount <= 0)
            return;
        LoadTreeFromAtkSnapshot(tree, _nativeAtkSnapshot, _nativeAtkSlotCount, _nativeTreeList);
        LogFilterDebug(nameof(RestoreNativeTreeFromSnapshot),
            $"restored native tree slots={_nativeAtkSlotCount} items.Count={tree->Items.Count}");
        _ = addon;
    }

    private void ReseedFilteredTreeFromNative() {
        if (_filteredTreeList is null || _nativeTreeList is null || _nativeAtkSnapshot.Length == 0 || _nativeAtkSlotCount <= 0)
            return;
        LoadTreeFromAtkSnapshot(_filteredTreeList, _nativeAtkSnapshot, _nativeAtkSlotCount, _nativeTreeList);
        LogFilterDebug(nameof(ReseedFilteredTreeFromNative),
            $"reseeded duplicate slots={_nativeAtkSlotCount} items.Count={_filteredTreeList->Items.Count}");
    }

    private static void ResetFilteredTreeScroll(AtkComponentTreeList* tree) {
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

    private static void ClearFilteredTreeDisplay(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        var list = (AtkComponentList*)tree;
        list->SetItemCount(0);
        ResetFilteredTreeScroll(tree);
        RefreshTreeListLayout(tree);
    }

    private static void LoadTreeFromAtkSnapshot(
        AtkComponentTreeList* tree,
        AtkValue[] atkSnapshot,
        int slotCount,
        AtkComponentTreeList* callbackSource = null) {
        var list = (AtkComponentList*)tree;
        var callback = list->CallBackInterface;
        if (callback is null && callbackSource is not null)
            callback = ((AtkComponentList*)callbackSource)->CallBackInterface;
        if (callback is null)
            return;
        fixed (AtkValue* snapshotPtr = atkSnapshot) {
            tree->LoadAtkValues(
                atkSnapshot.Length,
                snapshotPtr,
                CrystallizeListAtk.UintValuesOffset,
                CrystallizeListAtk.StringValuesOffset,
                CrystallizeListAtk.UintValuesPerItem,
                CrystallizeListAtk.StringValuesPerItem,
                slotCount,
                callback);
        }
        SyncTreeItemsFromAtk(tree, atkSnapshot, slotCount);
        RefreshTreeListLayout(tree);
    }

    private void ReleaseFilteredTree(AtkUnitBase* addon) {
        InvalidateTreeNodeCache();
        _filteredTreeReady = false;
        _filteredTreeDisplayed = false;
        _duplicateTreeNodeId = 0;
        _ = addon;
    }

    private void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        _displayToSource = [];
        _filteredAtkSlotCount = 0;
        data->CrystallizeItemCount = 0;
        data->CrystallizeItemIndex = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    // Rewrite agent CrystallizeItems[] so display index 0..N-1 maps to filtered source rows.
    // Native callbacks use these indices after we reload the tree; they must stay consistent with ATK u1.
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

    // Inverse of ProjectVisibleRows — used in OnPreRefresh so native refresh sees the full tab again.
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

    // Only restore our snapshot when the agent still holds our filtered projection. After crystallize or
    // other glamour mutations the game updates the agent first — restoring stale rows causes crashes.
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

    // Decide which _categoryRows indices pass GlamourLog filters. Does not touch ATK or the agent yet.
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

    // Predicate for CrystallizeListAtk.BuildKeepSlots leaf pass only.
    // Duplicate source indices can appear in the ATK layout when CrystallizeFilterFlags is on; keep the
    // first leaf slot per source index. Header visibility uses a separate path without seenSources —
    // sharing the predicate caused headers to "consume" a source and hide the real leaf row.
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
        if (_nativeAtkSnapshot.Length == 0) {
            _atkLayout = [];
            _nativeAtkSlotCount = 0;
            return;
        }
        var rowCount = categoryRowCount ?? _categoryRows.Length;
        if (force || _atkLayout.Length == 0) {
            var inferred = CrystallizeListAtk.InferItemCount(_nativeAtkSnapshot);
            var includeHeaders = rowCount > 0;
            _nativeAtkSlotCount = rowCount > 0
                ? CrystallizeListAtk.InferBoundedItemCount(_nativeAtkSnapshot, inferred, rowCount, includeHeaders)
                : inferred;
            _atkLayout = CrystallizeListAtk.Parse(_nativeAtkSnapshot, _nativeAtkSlotCount, rowCount);
        }
    }

    // Agent holds the full category but the tree still reflects our last filtered projection.
    private bool IsNativeTreeTruncatedVersusSnapshot(MiragePrismPrismBoxData* data) {
        if (_categoryRows.Length <= 0)
            return false;
        var listLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : (short)0;
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

    // Build or refresh _categoryRows from agent + native ATK after the game has refreshed the list.
    // Row count is derived from populated agent slots, tree listLength, and (when gearset filter is off)
    // the highest source index in the layout — gearset-on layouts can carry stale high indices we ignore.
    private bool TryCaptureCategorySnapshotAfterNative(MiragePrismPrismBoxData* data) {
        if (_nativeAtkSnapshot.Length == 0)
            return false;

        // Parse without categoryRowCount cap first so layoutMax reflects all leaf u1 values in the buffer.
        ParseAtkLayout(force: true, categoryRowCount: 0);
        if (_atkLayout.Length == 0)
            return false;

        var layoutMaxSource = GetLayoutMaxSourceIndex();
        var scannedCount = InferPopulatedCategoryItemCount(data);
        var listLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : (short)0;
        var rowCount = Math.Max(scannedCount, listLength);
        // Gearset filter off: native list can be sparse but ATK leaves still reference high source indices.
        // Gearset filter on: trust agent + listLength only — layoutMax often reflects a stale full-size buffer.
        if (_crystallizeFilterFlagsSnapshot == 0)
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

    // Agent slots can be empty for rows the game only materialized in ATK/tree (common after gearset toggles).
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
            if (CrystallizeListAtk.TryReadCategoryRow(_nativeAtkSnapshot, slot, entry, out var row))
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
        CaptureNativeListMetrics(addon);
    }

    private void CaptureNativeListMetrics(AtkUnitBase* addon) {
        if (addon is null)
            return;
        ResolveNativeTree(addon);
        if (_nativeTreeList is null)
            return;
        var list = (AtkComponentList*)_nativeTreeList;
        if (list->ItemHeight > 1)
            _nativeListItemHeight = list->ItemHeight;
        else if (list->RowStepY > 1)
            _nativeListItemHeight = (short)list->RowStepY;
        if (list->ListHeight > 0)
            _nativeListHeight = list->ListHeight;
        LogFilterDebug(nameof(CaptureNativeListMetrics),
            $"native itemHeight={list->ItemHeight} rowStepY={list->RowStepY} listHeight={list->ListHeight} numVisible={list->NumVisibleItems} numVisibleRows={list->NumVisibleRows} listLength={list->ListLength}");
    }

    // Compact the captured native ATK buffer to kept slots, then LoadAtkValues into node 11.
    // We clone before ApplyToBuffer because it mutates slot order in the array.
    private void RepopulateDisplayTreeList(AtkUnitBase* addon) {
        var tree = _nativeTreeList;
        if (tree is null) {
            return;
        }
        if (_nativeAtkSnapshot.Length == 0 || _atkLayout.Length == 0) {
            ClearFilteredTreeDisplay(tree);
            return;
        }
        var workingAtk = CrystallizeListAtk.Clone(_nativeAtkSnapshot);
        if (IsFilteringActive) {
            var visibleSources = new HashSet<int>(_displayToSource);
            _filteredAtkSlotCount = CrystallizeListAtk.ApplyToBuffer(
                workingAtk,
                _atkLayout,
                BuildShouldHideLeafPredicate(visibleSources),
                CrystallizeListAtk.UintValuesOffset,
                CrystallizeListAtk.StringValuesOffset,
                CrystallizeListAtk.UintValuesPerItem,
                CrystallizeListAtk.StringValuesPerItem,
                _nativeAtkSlotCount,
                IncludeSectionHeaders,
                visibleSources,
                ShouldExcludeSourceIndex);
        }
        if (_filteredAtkSlotCount <= 0) {
            var clearedAtk = CrystallizeListAtk.Clone(_nativeAtkSnapshot);
            var clearThrough = Math.Max(_nativeAtkSlotCount, 1);
            CrystallizeListAtk.ClearSlots(clearedAtk, 0, clearThrough);
            LoadTreeFromAtkSnapshot(tree, clearedAtk, 0, _nativeTreeList);
            ResetFilteredTreeScroll(tree);
            LogFilterDebug(nameof(RepopulateDisplayTreeList),
                $"cleared native tree (no filtered slots) clearedSlots={clearThrough} items.Count={tree->Items.Count}");
            return;
        }
        LoadTreeFromAtkSnapshot(tree, workingAtk, _filteredAtkSlotCount, _nativeTreeList);
        ResetFilteredTreeScroll(tree);
        RefreshTreeListLayout(tree);
        var list = (AtkComponentList*)tree;
        LogFilterDebug(nameof(RepopulateDisplayTreeList),
            $"loaded native filtered slots={_filteredAtkSlotCount} items.Count={tree->Items.Count} listLength={list->ListLength} numVisible={list->NumVisibleItems} itemHeight={list->ItemHeight} listHeight={list->ListHeight} hasRenderer={(nint)list->FirstAtkComponentListItemRenderer != 0} hasScrollBar={(nint)list->ScrollBarComponent != 0}");
        _ = addon;
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

    private static void ApplyFilteredListLayout(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        var list = (AtkComponentList*)tree;
        var count = (short)(tree->Items.Count > 0 ? tree->Items.Count : list->ListLength);
        if (count <= 0) {
            list->SetItemCount(0);
            return;
        }
        list->SetItemCount(0);
        list->SetItemCount(count);
        RefreshTreeListLayout(tree);
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

    private void ClearFilterLogSignatures() {
        _lastFilterSummarySignature = null;
        _lastPreFilterSignature = null;
        _lastPostFilterSignature = null;
        _lastHiddenItemsSignature = null;
        _lastApplyPipelineSignature = null;
        _lastFilterOffStateSignature = null;
        _lastFilterOnStateSignature = null;
    }

    private void LogFilterOnState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        ResolveNativeTree(addon);
        var displayTree = _nativeTreeList;
        var displayNode = _nativeTreeResNode;
        var nativeVisible = _nativeTreeResNode is not null && _nativeTreeResNode->IsVisible();
        var filteredItems = displayTree is not null ? displayTree->Items.Count : -1;
        var filteredListLength = displayTree is not null ? ((AtkComponentList*)displayTree)->ListLength : -1;
        var filteredNumVisible = displayTree is not null ? ((AtkComponentList*)displayTree)->NumVisibleItems : (short)-1;
        var filteredItemHeight = displayTree is not null ? ((AtkComponentList*)displayTree)->ItemHeight : (short)-1;
        var filteredListHeight = displayTree is not null ? ((AtkComponentList*)displayTree)->ListHeight : (short)-1;
        var filteredHasRenderer = displayTree is not null && ((AtkComponentList*)displayTree)->FirstAtkComponentListItemRenderer is not null;
        var filteredHasScrollBar = displayTree is not null && ((AtkComponentList*)displayTree)->ScrollBarComponent is not null;
        var summary =
            $"leaves={_displayToSource.Length} agentCount={data->CrystallizeItemCount} " +
            $"nativeNodeId={(_nativeTreeResNode is not null ? _nativeTreeResNode->NodeId : 0u)} nativeVisible={nativeVisible} " +
            $"nativeItems={filteredItems} nativeListLength={filteredListLength} nativeNumVisible={filteredNumVisible} " +
            $"itemHeight={filteredItemHeight} listHeight={filteredListHeight} hasRenderer={filteredHasRenderer} hasScrollBar={filteredHasScrollBar} " +
            $"displayTarget=native filteredTreeDisplayed={_filteredTreeDisplayed}";
        if (summary == _lastFilterOnStateSignature)
            return;
        _lastFilterOnStateSignature = summary;
        LogFilterDebug(phase, summary);
    }

    private void LogFilterOffState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        ResolveNativeTree(addon);
        RefreshFilteredTreePointers(addon);
        var sameNode = _nativeTreeResNode is not null && _nativeTreeResNode == _filteredTreeResNode;
        var nativeVisible = _nativeTreeResNode is not null && _nativeTreeResNode->IsVisible();
        var filteredVisible = _filteredTreeResNode is not null && _filteredTreeResNode->IsVisible();
        var nativeItems = _nativeTreeList is not null ? _nativeTreeList->Items.Count : -1;
        var nativeListLength = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->ListLength : (short)-1;
        var nativeGetItemCount = _nativeTreeList is not null ? ((AtkComponentList*)_nativeTreeList)->GetItemCount() : -1;
        var filteredItems = _filteredTreeList is not null ? _filteredTreeList->Items.Count : -1;
        var filteredListLength = _filteredTreeList is not null ? ((AtkComponentList*)_filteredTreeList)->ListLength : (short)-1;
        var filteredGetItemCount = _filteredTreeList is not null ? ((AtkComponentList*)_filteredTreeList)->GetItemCount() : -1;
        var summary =
            $"snapshot={_categoryRows.Length} agentCount={data->CrystallizeItemCount} inferred={InferCategoryItemCount(data)} " +
            $"nativeNodeId={(_nativeTreeResNode is not null ? _nativeTreeResNode->NodeId : 0u)} filteredNodeId={(_filteredTreeResNode is not null ? _filteredTreeResNode->NodeId : 0u)} sameNode={sameNode} filteredTreeDisplayed={_filteredTreeDisplayed} " +
            $"nativeVisible={nativeVisible} nativeItems={nativeItems} nativeListLength={nativeListLength} nativeGetItemCount={nativeGetItemCount} " +
            $"filteredVisible={filteredVisible} filteredItems={filteredItems} filteredListLength={filteredListLength} filteredGetItemCount={filteredGetItemCount} " +
            $"nativeAtkSlots={_nativeAtkSlotCount} filteredTreeReady={_filteredTreeReady} addonAtkValues={addon->AtkValuesCount}";
        if (summary == _lastFilterOffStateSignature)
            return;
        _lastFilterOffStateSignature = summary;
        LogFilterDebug(phase, summary);
    }

    // Fallback snapshot when PostRefresh has not run yet (e.g. first enable). Prefer
    // TryCaptureCategorySnapshotAfterNative when native ATK is available — it fills gaps from the tree.
    private void CaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        var agentCount = _nativeCategoryItemCount > 0 ? _nativeCategoryItemCount : InferCategoryItemCount(data);
        if (agentCount <= 0) {
            _categoryRows = [];
            _displayToSource = [];
            _needsCategorySnapshot = false;
            return;
        }
        // Agent is already filtered (CrystallizeItemCount < snapshot) — do not overwrite a good snapshot
        // with a truncated agent read; wait for PreRefresh restore + native refresh instead.
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

    // Gearset filter toggled mid-frame — recapture from current native output and refilter immediately
    // instead of waiting for the next Pre/Post refresh pair.
    private void ApplyFilterAfterFlagsChange(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!IsAddonUsable(addon) || !IsFilteringActive)
            return;

        ResolveNativeTree(addon);
        SetFilteredTreeActive(addon, active: false);
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

    // Scan for the last non-empty slot — CrystallizeItemCount alone is not reliable while we filter.
    private static int InferPopulatedCategoryItemCount(MiragePrismPrismBoxData* data) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }
        return lastIndex >= 0 ? lastIndex + 1 : 0;
    }

    // CrystallizeFilterFlags can change outside refresh; PrismBoxItemIds changes when glamour data updates.
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
        _filteredTreeList = null;
        _filteredTreeResNode = null;
        _nativeTreeList = null;
        _nativeTreeResNode = null;
        _filteredTreeReady = false;
        _filteredTreeDisplayed = false;
        _duplicateTreeNodeId = 0;
        _prismBoxItemIdsInitialized = false;
        _crystallizeFilterFlagsSnapshot = byte.MaxValue;
        _lastFilterSummarySignature = null;
        _lastPreFilterSignature = null;
        _lastPostFilterSignature = null;
        _lastHiddenItemsSignature = null;
        _lastApplyPipelineSignature = null;
        _lastFilterOffStateSignature = null;
        _lastFilterOnStateSignature = null;
    }

    private static bool IsAddonUsable(AtkUnitBase* addon)
        => addon is not null && addon->IsVisible;

    private static void LogFilterDebug(string phase, string message)
        => Svc.Log.Information($"[{nameof(CrystallizeListHandler)}.{phase}] {message}");

    // --- debug logging (signature-gated to avoid spamming identical state every frame) ---

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