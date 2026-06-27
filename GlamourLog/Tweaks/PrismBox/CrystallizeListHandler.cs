using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Controllers;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

// crystallize list filter: snapshot MiragePrismPrismBoxData rows per category tab, project visible subset, repopulate atk tree node 11
internal sealed partial class CrystallizeListHandler : IAsyncDisposable {
    private bool _disposed;
    private const int MaxCategoryItems = 140;
    private const int PrismBoxItemIdCount = 800;
    private const int CategorySlotCount = 6;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly CrystallizeNativeTree _nativeTree;
    private readonly AddonController<AtkUnitBase> _addonController;
    private readonly uint[] _prismBoxItemIdsSnapshot = new uint[PrismBoxItemIdCount];

    private bool _prismBoxItemIdsInitialized;
    private byte _crystallizeFilterFlagsSnapshot = byte.MaxValue;
    private PrismBoxCrystallizeItem[] _categoryRows = []; // full category snapshot from agent
    private int[] _displayToSource = []; // visible display index -> _categoryRows index
    private int _crystallizeCategory = int.MinValue; // tab currently being handled
    private int _snapshottedCategory = int.MinValue; // tab with committed snapshot; int.MinValue while capture pending
    private bool _needsCategorySnapshot;
    private int _refreshRecursionDepth; // suppress nested OnRefresh from RequestAddonRefresh
    private readonly bool[] _categoryNativeRefreshCompleted = new bool[CategorySlotCount];
    private readonly bool[] _awaitingNativePopulate = new bool[CategorySlotCount];
    private int _emptyCategoryRefreshPasses;
    private int _snapshotRetryPasses;
    private int _staleDisplayClearedForCategory = int.MinValue;
    private int _filterApplyDepth;
    private readonly int[] _nativePopulateCountByCategory = new int[CategorySlotCount];
    private readonly int[] _observedAgentCountByCategory = { -1, -1, -1, -1, -1, -1 };
    private readonly int[] _agentCountStablePassesByCategory = new int[CategorySlotCount];
    private readonly CachedCategorySnapshot?[] _categorySnapshotByIndex = new CachedCategorySnapshot?[CategorySlotCount]; // tab revisit cache
    private readonly HashSet<uint> _pendingDresserStoredBaseIds = []; // hide until dresser ownership catches up

    private sealed class CachedCategorySnapshot {
        public required PrismBoxCrystallizeItem[] Rows;
        public required byte FilterFlags;
    }

    public unsafe CrystallizeListHandler() {
        _filters = [
            new HideDresserDepositedFilter(),
            new HideArmoireEligibleFilter(),
            new HideNonOutfitItemsFilter(),
        ];
        _nativeTree = new CrystallizeNativeTree();
        _nativeTree.AfterNativePopulate = OnNativeTreePopulated;

        _addonController = new AddonController<AtkUnitBase> {
            AddonName = CrystallizeNativeTree.AddonName,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnUpdate = OnAddonUpdate,
            OnFinalize = OnFinalize,
        };
        _addonController.Enable();
        Svc.Log.Information($"[{nameof(CrystallizeListHandler)}] ready (filtering={IsFilteringActive})");
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool IsAwaitingNativePopulate(int categoryIndex)
        => IsValidCategory(categoryIndex) && _awaitingNativePopulate[categoryIndex];

    /// <summary>Hide freshly stored items in the filtered list until dresser ownership catches up.</summary>
    internal void NotifyItemsStored(IEnumerable<uint> itemIds) {
        foreach (var itemId in itemIds) {
            var baseId = ItemUtil.GetBaseId(itemId).ItemId;
            if (baseId != 0)
                _pendingDresserStoredBaseIds.Add(baseId);
        }

        RefreshFilteredCategoryDisplay();
    }

    /// <summary>Fallback target discovery when the agent buffer is empty or lagging behind the filter snapshot.</summary>
    internal bool TryGetNextVisibleStorableFromSnapshot(
        int categoryIndex,
        Func<PrismBoxCrystallizeItem, bool> isStorable,
        out PrismBoxCrystallizeItem row) {
        row = default;
        if (!IsFilteringActive || _snapshottedCategory != categoryIndex || _categoryRows.Length == 0)
            return false;

        RebuildFilterMap();

        foreach (var sourceIndex in _displayToSource) {
            if ((uint)sourceIndex >= (uint)_categoryRows.Length)
                continue;

            var candidate = _categoryRows[sourceIndex];
            if (!isStorable(candidate))
                continue;

            row = candidate;
            DresserStore.RefreshInventoryLocation(ref row);
            return true;
        }

        return false;
    }

    internal bool TryFindSnapshotRow(int categoryIndex, uint itemId, out PrismBoxCrystallizeItem row) {
        row = default;
        if (_snapshottedCategory != categoryIndex || _categoryRows.Length == 0)
            return false;

        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0)
            return false;

        foreach (var candidate in _categoryRows) {
            if (ItemUtil.GetBaseId(candidate.ItemId).ItemId != baseId)
                continue;

            row = candidate;
            return true;
        }

        return false;
    }

    /// <summary>True once the agent category tab has finished native load (and filter snapshot when filtering is on).</summary>
    internal unsafe bool IsCategoryUsableForStore(int categoryIndex) {
        if (!IsValidCategory(categoryIndex))
            return false;

        var data = GetData();
        if (data is null || data->CrystallizeCategory != categoryIndex)
            return false;

        if (!IsCategoryAgentBufferSettled(data))
            return false;

        if (!IsFilteringActive)
            return true;

        if (!_needsCategorySnapshot && _snapshottedCategory == categoryIndex)
            return true;

        var reported = (int)data->CrystallizeItemCount;
        var scanned = ScanPopulatedCategoryItemCount(data);
        if (reported == 0 && scanned == 0)
            return _categoryNativeRefreshCompleted[categoryIndex];

        return false;
    }

    private unsafe void RefreshFilteredCategoryDisplay() {
        if (!IsFilteringActive || _categoryRows.Length == 0)
            return;

        var addon = GetAddon();
        var data = GetData();
        if (addon is null || data is null || data->CrystallizeCategory != _snapshottedCategory)
            return;

        PrunePendingDresserStoredBaseIds();

        if (IsCrystallizeListUiStable() && TryApplyFilterPipeline(addon, data))
            return;

        RequestAddonRefresh(addon);
    }

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(ApplyConfigChange);
    }

    private unsafe void ApplyConfigChange() {
        var addon = GetAddon();
        var data = GetData();
        if (addon is null || data is null) {
            ClearFilterState();
            return;
        }

        if (!IsFilteringActive) {
            if (_categoryRows.Length > 0)
                RestoreAgentBufferFromSnapshot(data); // hand agent buffer back to native before filter-off refresh
            _displayToSource = [];
            ClearFilterLogSignatures();
            addon->OnRefresh(0, null);
            return;
        }

        EnsureCategoryTracked(data);

        if (_categoryRows.Length > 0)
            RestoreFullCategory(data); // undo projected agent buffer before re-filtering from snapshot

        _nativeTree.Resolve(addon);
        _nativeTree.CaptureAtkSnapshot(addon); // tree may still show prior filtered atk layout
        _nativeTree.ParseLayout(_categoryRows.Length, force: true);

        if (HasValidCategorySnapshot(data) && TryApplyFilterPipeline(addon, data))
            return;

        _needsCategorySnapshot = true;
        addon->OnRefresh(0, null);
    }

    private unsafe void OnPreRefresh(AtkUnitBase* addon) {
        if (!IsFilteringActive)
            return;

        var data = GetData();
        if (data is null)
            return;

        ResetFilterFlagsChangedThisRefresh();
        EnsureCategoryTracked(data);

        if (TryDetectFilterFlagsChange(data)) {
            _nativeTree.InvalidateAtkCache();
            _needsCategorySnapshot = true;
        }

        // Recapture needs the full category in the agent buffer, not the filtered projection left after a store.
        if (_categoryRows.Length > 0 && data->CrystallizeCategory == _snapshottedCategory && _needsCategorySnapshot)
            RestoreFullCategory(data);
        else if (ShouldRestoreAgentBufferBeforeNativeRefresh(data))
            RestoreAgentBufferFromSnapshot(data);
    }

    private unsafe void OnPostRefresh(AtkUnitBase* addon) {
        if (addon is null || !addon->IsVisible)
            return;

        var data = GetData();
        if (data is null) {
            LogMissingAgentDataOnce();
            return;
        }

        _refreshRecursionDepth++;
        try {
            if (!IsFilteringActive) {
                _nativeTree.Resolve(addon);
                _nativeTree.CaptureAtkSnapshot(addon); // keep layout cache warm for next filter-on
                if (_categoryRows.Length > 0 && _nativeTree.HasBufferLayout && _nativeTree.HasSnapshot)
                    _nativeTree.ParseLayout(_categoryRows.Length, force: true);
                _nativeTree.EnsureVisible(addon);
                LogFilterOffState(nameof(OnPostRefresh), addon, data);
                return;
            }

            ApplyFilterAfterNativeRefresh(addon, data);
        }
        finally {
            _refreshRecursionDepth--;
        }
    }

    private unsafe void ApplyFilterAfterNativeRefresh(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _nativeTree.Resolve(addon);
        EnsureCategoryTracked(data);
        TryDetectFilterFlagsChange(data);
        InvalidateSnapshotIfAgentStillLoading(data); // CrystallizeItemCount can grow across refreshes while native loads

        if (_needsCategorySnapshot && !TryCaptureCategorySnapshot(data)) {
            LogSnapshotUnavailableOnce(data);
            if (!TryCommitEmptyCategoryIfNativeSettled(addon, data))
                RequestSnapshotCaptureRetry(addon);
            else
                _snapshotRetryPasses = 0;

            if (IsCategoryAgentBufferSettled(data) && HasCommittedCategorySnapshot(data))
                TryApplyFilterPipeline(addon, data);
            else
                TryClearStaleCategoryDisplayOnce(addon, data);

            _nativeTree.EnsureVisible(addon, hideOtherTreeLists: true);
            LogFilterOnState(nameof(OnPostRefresh), addon, data);
            return;
        }

        _snapshotRetryPasses = 0;

        if (_categoryRows.Length == 0 || !IsCategoryAgentBufferSettled(data))
            return;

        TryApplyFilterPipeline(addon, data);
        LogFilterOnState(nameof(OnPostRefresh), addon, data);
    }

    private unsafe bool HasCommittedCategorySnapshot(MiragePrismPrismBoxData* data)
        => _categoryRows.Length > 0 && data->CrystallizeCategory == _snapshottedCategory;

    private unsafe bool TryCatchUpFilterState(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (TryCatchUpAfterPrematureEmptyCapture(addon, data))
            return true;

        if (_needsCategorySnapshot) {
            var populated = ScanPopulatedCategoryItemCount(data);
            if (populated > 0 || data->CrystallizeItemCount > 0) {
                ApplyFilterAfterNativeRefresh(addon, data);
                return true;
            }

            if (TryClearStaleCategoryDisplayOnce(addon, data)) {
                _nativeTree.EnsureVisible(addon, hideOtherTreeLists: true);
                return true;
            }

            return false;
        }

        if (!HasCommittedCategorySnapshot(data))
            return false;

        RebuildFilterMap();
        if (_displayToSource.Length >= _categoryRows.Length)
            return false;
        if (data->CrystallizeItemCount != _categoryRows.Length)
            return false;

        if (!IsCategoryAgentBufferSettled(data))
            return false;

        return TryApplyFilterPipeline(addon, data);
    }

    private unsafe bool TryCatchUpAfterPrematureEmptyCapture(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        var category = data->CrystallizeCategory;
        if (!IsValidCategory(category))
            return false;

        var nativeCount = _nativePopulateCountByCategory[category];
        if (nativeCount <= 0 && !IsSnapshotStale(data))
            return false;

        InvalidateStaleCategorySnapshot(data, nativeCount);
        if (_needsCategorySnapshot)
            TryCaptureCategorySnapshot(data);

        if (_categoryRows.Length > 0)
            return IsCategoryAgentBufferSettled(data) && TryApplyFilterPipeline(addon, data);

        return TryClearUnfilteredNativeDisplay(addon, data);
    }

    private unsafe bool TryClearUnfilteredNativeDisplay(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!IsCrystallizeListUiStable())
            return false;

        _nativeTree.Resolve(addon);
        var tree = _nativeTree.TreeList;
        if (tree is null)
            return false;

        var itemCount = ((AtkComponentList*)tree)->GetItemCount();
        if (itemCount <= 0)
            return false;

        _nativeTree.CaptureAtkSnapshot(addon);
        if (_nativeTree.HasBufferLayout)
            _nativeTree.ParseLayout(0, force: true);
        _nativeTree.ClearStaleDisplay(addon);
        _nativeTree.EnsureVisible(addon, hideOtherTreeLists: true);
        _staleDisplayClearedForCategory = data->CrystallizeCategory;
        return true;
    }

    private unsafe bool IsSnapshotStale(MiragePrismPrismBoxData* data, int nativeHint = 0) {
        if (_needsCategorySnapshot || _snapshottedCategory != data->CrystallizeCategory)
            return false;

        var scanned = ScanPopulatedCategoryItemCount(data);
        var reported = (int)data->CrystallizeItemCount;
        var agentCount = Math.Max(reported, scanned);
        var snapshotLen = _categoryRows.Length;

        if (snapshotLen == 0)
            return agentCount > 0 || nativeHint > 0;

        if (agentCount == 0)
            return false;

        return agentCount > snapshotLen;
    }

    private unsafe void InvalidateStaleCategorySnapshot(MiragePrismPrismBoxData* data, int nativeHint = 0) {
        if (!IsSnapshotStale(data, nativeHint))
            return;

        var category = data->CrystallizeCategory;
        InvalidateCategorySnapshotCache(category);
        _categoryRows = [];
        _displayToSource = [];
        _needsCategorySnapshot = true;
        _snapshottedCategory = int.MinValue;
        _emptyCategoryRefreshPasses = 0;
        _staleDisplayClearedForCategory = int.MinValue;
        MarkCategoryAwaitingNativeRefresh(category);
        _lastSnapshotUnavailableSignature = null;
    }

    private unsafe bool TryClearStaleCategoryDisplayOnce(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!IsCrystallizeListUiStable())
            return false;
        if (data->CrystallizeCategory == _staleDisplayClearedForCategory)
            return false;

        _nativeTree.Resolve(addon);
        var tree = _nativeTree.TreeList;
        if (tree is null)
            return false;

        var itemCount = ((AtkComponentList*)tree)->GetItemCount();
        if (itemCount <= 0 && data->CrystallizeItemCount == 0)
            return false;

        _nativeTree.ClearStaleDisplay(addon);
        _staleDisplayClearedForCategory = data->CrystallizeCategory;
        return true;
    }

    private unsafe void RequestSnapshotCaptureRetry(AtkUnitBase* addon) {
        if (_refreshRecursionDepth > 1)
            return;
        if (++_snapshotRetryPasses > 12)
            return;
        RequestAddonRefresh(addon);
    }

    private unsafe void InvalidateSnapshotIfAgentStillLoading(MiragePrismPrismBoxData* data) {
        InvalidateStaleCategorySnapshot(data);
    }

    private unsafe void OnAddonUpdate(AtkUnitBase* addon) {
        if (addon is null || !addon->IsVisible || !IsFilteringActive)
            return;

        var data = GetData();
        if (data is null)
            return;

        if (TryDetectCategoryDrift(addon, data)) // tab switch without OnRefresh (e.g. 5→0)
            return;

        if (TryDetectFilterFlagsChange(data)) { // in-addon category filter toggles
            ApplyFilterAfterFlagsChange(addon, data);
            return;
        }

        if (TryCatchUpFilterState(addon, data))
            return;

        var mirage = MirageManager.Instance();
        if (mirage is null || !TryDetectPrismBoxItemIdsChange(mirage)) // dresser deposit / prism box contents changed
            return;

        _needsCategorySnapshot = true;
        _nativeTree.InvalidateAtkCache();
        ClearCategorySnapshotCache();
        ClearFilterLogSignatures();
        RequestAddonRefresh(addon);
    }

    private unsafe void OnFinalize(AtkUnitBase* addon) {
        if (addon is not null) {
            _nativeTree.EnsureVisible(addon);
            var data = GetData();
            if (data is not null && _categoryRows.Length > 0)
                RestoreFullCategory(data); // leave agent buffer unfiltered for native teardown
        }
        _nativeTree.InvalidateAll();
        ClearFilterState();
    }

    private unsafe bool TryApplyFilterPipeline(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_filterApplyDepth > 0)
            return false;

        _filterApplyDepth++;
        try {
            if (_categoryRows.Length == 0 || !IsCategoryAgentBufferSettled(data))
                return false;

            _nativeTree.Resolve(addon);
            if (_nativeTree.TreeList is null)
                return false;

            RebuildFilterMap(); // _displayToSource from _categoryRows + filters

            if (_displayToSource.Length == 0)
                ApplyEmptyCategory(data); // all rows hidden
            else
                ProjectVisibleRows(data); // compact agent CrystallizeItems to visible subset

            // setconvert open collapses the list — skip atk repopulate until we're back on crystallize
            if (!IsCrystallizeListUiStable()) {
                _nativeTree.EnsureVisible(addon, hideOtherTreeLists: true);
                return true;
            }

            _nativeTree.CaptureAtkSnapshot(addon);
            if (!_nativeTree.HasBufferLayout) {
                RequestAddonRefresh(addon);
                return false;
            }

            _nativeTree.ParseLayout(_categoryRows.Length, force: true);
            if (!_nativeTree.HasLayout) {
                RequestAddonRefresh(addon);
                return false;
            }

            RepopulateDisplayTree(addon); // mirror projected rows into atk tree
            _nativeTree.EnsureVisible(addon, hideOtherTreeLists: true);
            LogApplyPipelineResult(_nativeTree.TreeList, data);
            return true;
        }
        finally {
            _filterApplyDepth--;
        }
    }

    private unsafe void OnNativeTreePopulated(AtkUnitBase* addon, int nativeItemCount) {
        if (_filterApplyDepth > 0 || !IsFilteringActive || addon is null || !addon->IsVisible || nativeItemCount <= 0)
            return;

        var data = GetData();
        if (data is null)
            return;

        EnsureCategoryTracked(data);
        var category = data->CrystallizeCategory;
        if (IsValidCategory(category)) {
            if (_nativePopulateCountByCategory[category] != nativeItemCount)
                ResetAgentCountStability(category);
            _nativePopulateCountByCategory[category] = nativeItemCount;
            _awaitingNativePopulate[category] = false;
        }

        LogNativeTreePopulated(data, nativeItemCount);

        InvalidateStaleCategorySnapshot(data, nativeItemCount);

        if (!IsCategoryAgentBufferSettled(data)) {
            if (_needsCategorySnapshot)
                TryCaptureCategorySnapshot(data);

            if (_categoryRows.Length == 0)
                TryClearUnfilteredNativeDisplay(addon, data);
            return;
        }

        if (_needsCategorySnapshot)
            TryCaptureCategorySnapshot(data);

        if (_categoryRows.Length == 0) {
            TryClearUnfilteredNativeDisplay(addon, data);
            return;
        }

        if (data->CrystallizeCategory != _snapshottedCategory)
            return;

        TryApplyFilterPipeline(addon, data);
    }

    private unsafe bool TryCaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        if (!IsCategoryAgentBufferSettled(data))
            return false;

        var category = data->CrystallizeCategory;
        ClearStaleCrystallizeTail(data);
        var reported = (int)data->CrystallizeItemCount;
        var scanned = ScanPopulatedCategoryItemCount(data);
        if (reported == 0) {
            if (scanned == 0)
                return false;
            reported = scanned; // count field can lag behind populated slots during native load
        }

        for (var i = 0; i < reported; i++) {
            if (data->CrystallizeItems[i].ItemId == 0) // wait until native fills every slot
                return false;
        }

        if (scanned > reported)
            return false;

        if (_categoryRows.Length > 0 && data->CrystallizeCategory == _snapshottedCategory) {
            // allow one row to drop after a store; bigger drops are partial native refreshes
            var minExpected = _categoryRows.Length - 1;
            if (reported < minExpected && scanned < minExpected)
                return false;
        }

        return CommitCapturedCategory(data, reported);
    }

    private unsafe bool CommitCapturedCategory(MiragePrismPrismBoxData* data, int count) {
        var rows = new PrismBoxCrystallizeItem[count];
        for (var i = 0; i < count; i++)
            rows[i] = data->CrystallizeItems[i];

        FinalizeCategoryCapture(data, rows);
        SaveCategorySnapshotToCache(data);
        return true;
    }

    private unsafe bool TryCommitEmptyCategoryIfNativeSettled(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (data->CrystallizeCategory != _crystallizeCategory)
            return false;

        var category = data->CrystallizeCategory;
        if (IsValidCategory(category) && _awaitingNativePopulate[category]) {
            if (_emptyCategoryRefreshPasses + _snapshotRetryPasses < 5)
                return false;
            _awaitingNativePopulate[category] = false;
        }

        if (IsValidCategory(category) && _nativePopulateCountByCategory[category] > 0) {
            _emptyCategoryRefreshPasses = 0;
            return false; // native tree already populated — agent buffer is still catching up
        }

        var reported = data->CrystallizeItemCount;
        var scanned = InferPopulatedCategoryItemCount(data);
        if (reported != 0 || scanned != 0) {
            _emptyCategoryRefreshPasses = 0;
            return false;
        }

        _nativeTree.Resolve(addon);
        _nativeTree.CaptureAtkSnapshot(addon);
        if (addon->AtkValuesCount > 0)
            _nativeTree.ParseLayout(0, force: true);

        if (_nativeTree.NativeSlotCount > 0) {
            _emptyCategoryRefreshPasses = 0;
            return false;
        }

        _emptyCategoryRefreshPasses++;
        if (_emptyCategoryRefreshPasses >= 2) {
            CommitEmptyCategory(data);
            return true;
        }

        RequestAddonRefresh(addon); // empty tabs need a second pass before we commit
        return false;
    }

    private unsafe void CommitEmptyCategory(MiragePrismPrismBoxData* data)
        => FinalizeCategoryCapture(data, []);

    private unsafe void FinalizeCategoryCapture(MiragePrismPrismBoxData* data, PrismBoxCrystallizeItem[] rows) {
        _categoryRows = rows;
        _needsCategorySnapshot = false;
        _snapshottedCategory = data->CrystallizeCategory;
        _crystallizeCategory = data->CrystallizeCategory;
        _emptyCategoryRefreshPasses = 0;
        _snapshotRetryPasses = 0;
        _staleDisplayClearedForCategory = int.MinValue;
        _lastSnapshotUnavailableSignature = null;
        MarkCategoryNativeRefreshCompleted(data->CrystallizeCategory);
    }

    private void MarkCategoryNativeRefreshCompleted(int category) {
        if (IsValidCategory(category))
            _categoryNativeRefreshCompleted[category] = true;
    }

    private unsafe void ApplyFilterAfterFlagsChange(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_categoryRows.Length > 0)
            RestoreAgentBufferFromSnapshot(data); // TryDetectFilterFlagsChange already cleared snapshot state
        RequestAddonRefresh(addon);
    }

    private unsafe void RepopulateDisplayTree(AtkUnitBase* addon) {
        var visibleSources = new HashSet<int>(_displayToSource);
        _nativeTree.RepopulateFiltered(addon, IsFilteringActive, _displayToSource.Length, visibleSources, BuildShouldHideLeafPredicate(visibleSources), _ => false);
    }

    private unsafe void SaveCategorySnapshotToCache(MiragePrismPrismBoxData* data) {
        var category = data->CrystallizeCategory;
        if (!IsValidCategory(category) || _categoryRows.Length == 0)
            return;

        _categorySnapshotByIndex[category] = new CachedCategorySnapshot {
            Rows = [.. _categoryRows],
            FilterFlags = data->CrystallizeFilterFlags,
        };
    }

    private unsafe bool TryRestoreCategorySnapshotFromCache(MiragePrismPrismBoxData* data, int category) {
        if (!IsValidCategory(category))
            return false;

        var cached = _categorySnapshotByIndex[category];
        if (cached is null || cached.Rows.Length == 0)
            return false;
        if (cached.FilterFlags != data->CrystallizeFilterFlags) // in-addon filter toggles invalidate cache entry
            return false;

        var scanned = ScanPopulatedCategoryItemCount(data);
        var reported = (int)data->CrystallizeItemCount;
        var agentCount = Math.Max(reported, scanned);
        if (agentCount != cached.Rows.Length)
            return false;

        _categoryRows = [.. cached.Rows];
        _crystallizeCategory = category;
        _displayToSource = [];
        return true;
    }

    private void ClearCategorySnapshotCache() {
        for (var i = 0; i < CategorySlotCount; i++)
            _categorySnapshotByIndex[i] = null;
    }

    private void InvalidateCategorySnapshotCache(int category) {
        if (IsValidCategory(category))
            _categorySnapshotByIndex[category] = null;
    }

    private static bool IsValidCategory(int category)
        => (uint)category < CategorySlotCount;

    private void ResetCategoryCaptureState() {
        _categoryRows = [];
        _snapshottedCategory = int.MinValue;
        _needsCategorySnapshot = true;
        _displayToSource = [];
        if (IsValidCategory(_crystallizeCategory))
            MarkCategoryAwaitingNativeRefresh(_crystallizeCategory);
    }

    private void MarkCategoryAwaitingNativeRefresh(int category) {
        if (IsValidCategory(category))
            _categoryNativeRefreshCompleted[category] = false;
    }

    private void ResetAgentCountStability(int category) {
        if (!IsValidCategory(category))
            return;

        _observedAgentCountByCategory[category] = -1;
        _agentCountStablePassesByCategory[category] = 0;
    }

    private unsafe bool IsCategoryAgentBufferSettled(MiragePrismPrismBoxData* data) {
        var category = data->CrystallizeCategory;
        var reported = (int)data->CrystallizeItemCount;
        var scanned = ScanPopulatedCategoryItemCount(data);

        if (reported == 0 && scanned == 0) {
            if (IsValidCategory(category) && _awaitingNativePopulate[category])
                return _nativePopulateCountByCategory[category] > 0;
            return true;
        }

        if (IsValidCategory(category) && _nativePopulateCountByCategory[category] <= 0)
            return false;

        if (reported != scanned)
            return false;

        for (var i = 0; i < reported; i++) {
            if (data->CrystallizeItems[i].ItemId == 0)
                return false;
        }

        return ObserveAgentCountStability(category, reported);
    }

    private bool ObserveAgentCountStability(int category, int agentCount) {
        if (!IsValidCategory(category))
            return agentCount == 0;

        if (_observedAgentCountByCategory[category] != agentCount) {
            _observedAgentCountByCategory[category] = agentCount;
            _agentCountStablePassesByCategory[category] = 0;
            return false;
        }

        _agentCountStablePassesByCategory[category]++;
        return _agentCountStablePassesByCategory[category] >= 2;
    }

    private void BeginCategoryRecapture(int category, bool setTrackedCategory, bool clearLogSignatures) {
        if (!IsValidCategory(category))
            return;

        MarkCategoryAwaitingNativeRefresh(category);
        ResetAgentCountStability(category);
        _awaitingNativePopulate[category] = true;
        InvalidateCategorySnapshotCache(category);
        if (setTrackedCategory)
            _crystallizeCategory = category;
        ResetCategoryCaptureState();
        _emptyCategoryRefreshPasses = 0;
        _snapshotRetryPasses = 0;
        _nativeTree.InvalidateAtkCache();
        if (clearLogSignatures)
            ClearFilterLogSignatures();
    }

    private unsafe void EnsureCategoryTracked(MiragePrismPrismBoxData* data) {
        var category = data->CrystallizeCategory;

        if (!_needsCategorySnapshot && category == _snapshottedCategory && _categoryRows.Length > 0)
            return; // committed snapshot for current tab

        if (category == _crystallizeCategory) {
            InvalidateStaleCategorySnapshot(data);
            return; // already handling this tab (capture may still be pending)
        }

        _crystallizeCategory = category;
        _displayToSource = [];
        _emptyCategoryRefreshPasses = 0;
        _staleDisplayClearedForCategory = int.MinValue;
        if (IsValidCategory(category)) {
            _nativePopulateCountByCategory[category] = 0;
            _awaitingNativePopulate[category] = true;
            ResetAgentCountStability(category);
        }
        MarkCategoryAwaitingNativeRefresh(category);

        if (TryRestoreCategorySnapshotFromCache(data, category)) {
            _snapshottedCategory = category;
            _needsCategorySnapshot = false;
            MarkCategoryNativeRefreshCompleted(category);
            _awaitingNativePopulate[category] = false;
            return;
        }

        ResetCategoryCaptureState();
        ClearStaleCrystallizeTail(data);
        _nativeTree.InvalidateAtkCache();
        ClearFilterLogSignatures();
    }

    private unsafe bool TryDetectCategoryDrift(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_needsCategorySnapshot || _categoryRows.Length == 0)
            return false;
        if (data->CrystallizeCategory == _snapshottedCategory)
            return false;

        EnsureCategoryTracked(data);
        RequestAddonRefresh(addon);
        return true;
    }

    private static unsafe void ClearStaleCrystallizeTail(MiragePrismPrismBoxData* data) {
        // zero slots past CrystallizeItemCount so Infer* / comparisons don't see prior tab tail
        var reported = data->CrystallizeItemCount;
        for (var i = reported; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private void RebuildFilterMap() {
        PrunePendingDresserStoredBaseIds();

        if (_categoryRows.Length == 0) {
            _displayToSource = [];
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

    private unsafe void ProjectVisibleRows(MiragePrismPrismBoxData* data) {
        var visible = _displayToSource.Length;
        var selectedItemId = data->CrystallizeSelectedItem.ItemId;
        for (var displayIndex = 0; displayIndex < visible; displayIndex++) {
            var sourceIndex = _displayToSource[displayIndex];
            if ((uint)sourceIndex < (uint)_categoryRows.Length)
                data->CrystallizeItems[displayIndex] = _categoryRows[sourceIndex];
        }
        data->CrystallizeItemCount = (ushort)visible;
        for (var i = visible; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
        ClampCrystallizeSelection(data, selectedItemId); // keep selection if still visible after filter
    }

    private unsafe void RestoreAgentBufferFromSnapshot(MiragePrismPrismBoxData* data) {
        var count = _categoryRows.Length;
        for (var i = 0; i < count; i++)
            data->CrystallizeItems[i] = _categoryRows[i];
        data->CrystallizeItemCount = (ushort)count;
        for (var i = count; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private unsafe void RestoreFullCategory(MiragePrismPrismBoxData* data) {
        RestoreAgentBufferFromSnapshot(data);
        _displayToSource = [.. Enumerable.Range(0, _categoryRows.Length)];
    }

    private unsafe void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        data->CrystallizeItemCount = 0;
        data->CrystallizeItemIndex = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private unsafe bool AgentMatchesCategorySnapshot(MiragePrismPrismBoxData* data) {
        // agent still holds full category (not a prior filtered projection)
        var agentCount = data->CrystallizeItemCount;
        if (agentCount != _categoryRows.Length)
            return false;
        for (var i = 0; i < agentCount; i++) {
            if (data->CrystallizeItems[i].ItemId != _categoryRows[i].ItemId)
                return false;
        }
        return true;
    }

    private unsafe bool ShouldRestoreAgentBufferBeforeNativeRefresh(MiragePrismPrismBoxData* data) {
        if (_categoryRows.Length == 0 || _needsCategorySnapshot)
            return false;
        if (data->CrystallizeCategory != _snapshottedCategory)
            return false;
        if (AgentMatchesCategorySnapshot(data))
            return false;

        // Only undo ProjectVisibleRows — when agent count matches our filtered display map.
        if (_displayToSource.Length == 0)
            return false;

        var agentCount = data->CrystallizeItemCount;
        if (agentCount != _displayToSource.Length)
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

    private bool ShouldExcludeLeaf(uint itemId) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId != 0 && _pendingDresserStoredBaseIds.Contains(baseId))
            return true;

        return _filters.Any(f => f.IsEnabled && f.ShouldHide(baseId));
    }

    private void PrunePendingDresserStoredBaseIds() {
        if (_pendingDresserStoredBaseIds.Count == 0)
            return;

        var ownership = Svc.Get<OwnershipService>();
        _pendingDresserStoredBaseIds.RemoveWhere(ownership.IsCrystallizeItemFullyDeposited);
    }

    private static unsafe bool IsCrystallizeListUiStable() {
        if (!AtkUnitBase.IsAddonReady(CrystallizeNativeTree.AddonName))
            return false;

        if (AtkUnitBase.IsAddonReady("MiragePrismPrismSetConvert"))
            return false;

        if (Svc.GameGui.TryGetAddon<AtkUnitBase>("MiragePrismPrismSetConvertC", out var confirm) && confirm->IsVisible)
            return false;

        return true;
    }

    private Func<int, bool> BuildShouldHideLeafPredicate(HashSet<int> visibleSources) {
        var seenSources = new HashSet<int>();
        return sourceIndex => !visibleSources.Contains(sourceIndex) || !seenSources.Add(sourceIndex); // atk layout can reference same source twice
    }

    private unsafe bool HasValidCategorySnapshot(MiragePrismPrismBoxData* data)
        => data->CrystallizeCategory == _snapshottedCategory && _categoryRows.Length > 0 && AgentMatchesCategorySnapshot(data);

    private unsafe bool TryDetectFilterFlagsChange(MiragePrismPrismBoxData* data) {
        if (_crystallizeFilterFlagsSnapshot == byte.MaxValue) {
            SyncFilterFlagsSnapshot(data); // first observation
            return false;
        }
        if (data->CrystallizeFilterFlags == _crystallizeFilterFlagsSnapshot)
            return false;
        SyncFilterFlagsSnapshot(data);
        MarkFilterFlagsChangedThisRefresh();
        ClearCategorySnapshotCache();
        ResetCategoryCaptureState();
        ClearFilterLogSignatures();
        return true;
    }

    private unsafe void SyncFilterFlagsSnapshot(MiragePrismPrismBoxData* data) {
        _crystallizeFilterFlagsSnapshot = data->CrystallizeFilterFlags;
    }

    private unsafe bool TryDetectPrismBoxItemIdsChange(MirageManager* mirage) {
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

    private unsafe void RequestAddonRefresh(AtkUnitBase* addon) {
        if (_refreshRecursionDepth > 1) // already inside OnPostRefresh
            return;
        addon->OnRefresh(0, null);
    }

    private static unsafe void ClampCrystallizeSelection(MiragePrismPrismBoxData* data, uint previousSelectedItemId) {
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

    private static unsafe AtkUnitBase* GetAddon()
        => Svc.GameGui.GetAddonByName<AtkUnitBase>(CrystallizeNativeTree.AddonName);

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }

    private static unsafe int InferPopulatedCategoryItemCount(MiragePrismPrismBoxData* data)
        => ScanPopulatedCategoryItemCount(data);

    private static unsafe int ScanPopulatedCategoryItemCount(MiragePrismPrismBoxData* data) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }

        if (lastIndex >= 0)
            return lastIndex + 1;

        return data->CrystallizeItemCount > 0 ? data->CrystallizeItemCount : 0;
    }

    private void ClearFilterState() {
        _categoryRows = [];
        _displayToSource = [];
        _crystallizeCategory = int.MinValue;
        _snapshottedCategory = int.MinValue;
        _needsCategorySnapshot = false;
        _emptyCategoryRefreshPasses = 0;
        _snapshotRetryPasses = 0;
        Array.Clear(_categoryNativeRefreshCompleted);
        Array.Clear(_nativePopulateCountByCategory);
        Array.Clear(_awaitingNativePopulate);
        Array.Fill(_observedAgentCountByCategory, -1);
        Array.Clear(_agentCountStablePassesByCategory);
        _staleDisplayClearedForCategory = int.MinValue;
        _prismBoxItemIdsInitialized = false;
        _crystallizeFilterFlagsSnapshot = byte.MaxValue;
        ClearCategorySnapshotCache();
        _pendingDresserStoredBaseIds.Clear();
        _nativeTree.InvalidateAll();
        ClearFilterLogSignatures();
    }

    public async ValueTask DisposeAsync() {
        if (_disposed)
            return;
        _disposed = true;
        await Svc.Framework.RunOnFrameworkThread(() => {
            _nativeTree.AfterNativePopulate = null;
            _addonController.Dispose();
            _nativeTree.Dispose();
            ClearFilterState();
        });
    }
}
