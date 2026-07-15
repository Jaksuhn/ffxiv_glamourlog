using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed partial class CrystallizeListHandler : IAsyncDisposable {
    private const int MaxCategoryItems = 140;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly CrystallizeNativeTree _nativeTree;
    private readonly AddonController<AtkUnitBase> _addonController;

    private PrismBoxCrystallizeItem[] _categoryRows = []; // full native rows; restored before each native refresh
    private int[] _displayToSource = []; // source indices of visible rows when filtering
    private int _trackedCategory = int.MinValue;
    private bool _disposed;
    private byte _filterFlagsSnapshot = byte.MaxValue; // native ui filter flags (outfit type etc)
    private int _refreshRecursionDepth;
    private int _emptyCategoryRefreshPasses;
    private int _deferredRefreshDepth;
    private int _snapshotCapturePasses;
    private bool _deferCategoryRestore; // skip restore until native flags settle after flag change
    private bool _needsUnfilteredRepopulate; // one-shot baseline repopulate after filter disable
    private bool _refreshScheduled;
    private bool _populationWasIncomplete; // re-refresh when native finishes loading mid-filter

    public unsafe CrystallizeListHandler() {
        _filters = PrismBoxFilters.Create();
        _nativeTree = new CrystallizeNativeTree();
        _addonController = new AddonController<AtkUnitBase> {
            AddonName = CrystallizeNativeTree.AddonName,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnUpdate = OnAddonUpdate,
            OnFinalize = OnFinalize,
        };
        _addonController.Enable();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    internal void OnConfigChanged() => Svc.Framework.RunOnFrameworkThread(ApplyConfigChange);

    internal IDisposable DeferRefresh() {
        _deferredRefreshDepth++;
        return new DeferredRefreshScope(this);
    }

    internal unsafe void NotifyItemStored(uint itemId) {
        if (ItemUtil.GetBaseId(itemId).ItemId == 0)
            return;

        if (_deferredRefreshDepth > 0)
            return;

        var addon = GetAddon();
        if (addon is not null)
            RequestAddonRefresh(addon);
    }

    private void FlushDeferredRefresh() {
        if (--_deferredRefreshDepth > 0)
            return;

        Svc.Framework.RunOnFrameworkThread(ApplyDeferredRefresh);
    }

    private unsafe void ApplyDeferredRefresh() {
        var addon = GetAddon();
        if (addon is not null)
            RequestAddonRefresh(addon);
    }

    private sealed class DeferredRefreshScope(CrystallizeListHandler owner) : IDisposable {
        public void Dispose() => owner.FlushDeferredRefresh();
    }

    private unsafe void ApplyConfigChange() {
        var addon = GetAddon();
        if (addon is null) {
            ClearFilterState();
            return;
        }

        var data = GetData();
        if (data is null) {
            ClearFilterState();
            return;
        }

        if (!IsFilteringActive) {
            // restore agent array before refresh so capture sees full category, not projected count
            if (_categoryRows.Length > 0 && data->CrystallizeCategory == _trackedCategory)
                RestoreFullCategory(data);
            _nativeTree.InvalidateAtkCache();
            _needsUnfilteredRepopulate = true;
        }
        else if (!_nativeTree.IsBaselineValidFor(data->CrystallizeCategory, _categoryRows.Length)) {
            _nativeTree.InvalidateAtkCache();
        }

        LogFilterDebug(nameof(ApplyConfigChange), IsFilteringActive
            ? $"filters enabled for category {data->CrystallizeCategory}"
            : $"filters disabled for category {data->CrystallizeCategory}");

        addon->OnRefresh(0, null);
    }

    private unsafe void OnPreRefresh(AtkUnitBase* addon) {
        var data = GetData();
        if (data is null)
            return;

        // if category already flipped before this refresh, wipe projected leftovers so native rebuilds fully
        if (TryHandlePreRefreshCategoryChange(data))
            return;

        if (!IsFilteringActive) {
            PrepareForNativeRefresh(data);
            return;
        }

        if (TryDetectFilterFlagsChange(data)) {
            _nativeTree.InvalidateAtkCache();
            _nativeTree.InvalidateBaseline();
            _deferCategoryRestore = true;
            ClearTransientState();
        }

        PrepareForNativeRefresh(data);
    }

    private unsafe void OnPostRefresh(AtkUnitBase* addon) {
        if (addon is null || !addon->IsVisible)
            return;

        var data = GetData();
        if (data is null)
            return;

        _refreshRecursionDepth++;
        try {
            EnsureCategoryTracked(data);

            if (TryDetectFilterFlagsChange(data)) {
                _nativeTree.InvalidateAtkCache();
                _nativeTree.InvalidateBaseline();
                _deferCategoryRestore = true;
                ClearTransientState();
                RequestAddonRefresh(addon);
                return;
            }

            if (!IsFilteringActive) {
                ApplyUnfilteredDisplay(addon, data);
                return;
            }

            ApplyFilteredDisplay(addon, data);
        }
        finally {
            _refreshRecursionDepth--;
        }
    }

    private unsafe void ApplyFilteredDisplay(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!TryCaptureCategorySnapshot(data)) {
            LogSnapshotUnavailableOnce(data);
            TryCommitEmptyCategoryIfNativeSettled(addon, data);
            if (++_snapshotCapturePasses < 8)
                RequestAddonRefresh(addon);
            return;
        }

        _snapshotCapturePasses = 0;

        if (!TryEnsureBaselineFromNative(addon, data)) {
            LogFilterWarning($"baseline capture stalled for category {data->CrystallizeCategory}");
            RequestAddonRefresh(addon);
            return;
        }

        if (!TryApplyFilterPipeline(addon, data)) {
            LogFilterWarning($"filter pipeline stalled for category {data->CrystallizeCategory}");
            RequestAddonRefresh(addon);
            return;
        }

        LogFilterOnState(nameof(OnPostRefresh), addon, data);
    }

    private unsafe void ApplyUnfilteredDisplay(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _displayToSource = [];

        if (!TryCaptureCategorySnapshot(data)) {
            LogSnapshotUnavailableOnce(data);
            if (++_snapshotCapturePasses < 8)
                RequestAddonRefresh(addon);
            return;
        }

        _snapshotCapturePasses = 0;
        RestoreFullCategory(data);

        if (!TryEnsureBaselineFromNative(addon, data) && !_nativeTree.HasBaseline) {
            LogFilterWarning($"baseline capture stalled for category {data->CrystallizeCategory}");
            RequestAddonRefresh(addon);
            return;
        }

        // only repopulate atk tree when recovering from filter-off; passive capture preserves native headers
        if (_needsUnfilteredRepopulate) {
            if (!TryRepopulateTree(addon, data, isFilteringActive: false)) {
                LogFilterWarning($"unfiltered display stalled for category {data->CrystallizeCategory}");
                RequestAddonRefresh(addon);
                return;
            }

            _needsUnfilteredRepopulate = false;
        }

        LogFilterOffState(nameof(OnPostRefresh), addon, data);
    }

    private unsafe void OnAddonUpdate(AtkUnitBase* addon) {
        if (addon is null || !addon->IsVisible)
            return;

        var data = GetData();
        if (data is null)
            return;

        if (TryDetectCategoryDrift(addon, data))
            return;

        var populationIncomplete = data->IsPopulatingList || !data->IsPopulatingComplete;
        if (IsFilteringActive && data->CrystallizeCategory == _trackedCategory
            && _populationWasIncomplete && !populationIncomplete) {
            RequestAddonRefresh(addon); // native finished loading after we projected too early
        }
        _populationWasIncomplete = populationIncomplete;

        if (TryDetectFilterFlagsChange(data)) {
            _nativeTree.InvalidateAtkCache();
            _nativeTree.InvalidateBaseline();
            _deferCategoryRestore = true;
            ClearTransientState();
            RequestAddonRefresh(addon);
        }
    }

    private unsafe bool TryCaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        if (data->CrystallizeCategory != _trackedCategory)
            return false;

        // avoid capturing the previous tab's projected rows while native is still rebuilding
        if (data->IsPopulatingList || !data->IsPopulatingComplete)
            return false;

        ClearStaleCrystallizeTail(data);

        var reported = (int)data->CrystallizeItemCount;
        if (reported == 0)
            return false;

        for (var i = 0; i < reported; i++) {
            if (data->CrystallizeItems[i].ItemId == 0)
                return false; // holes mean native still filling the array
        }

        var scanned = ScanPopulatedCategoryItemCount(data);
        if (scanned > reported)
            return false; // items ahead of count — still loading

        var rows = new PrismBoxCrystallizeItem[reported];
        for (var i = 0; i < reported; i++)
            rows[i] = data->CrystallizeItems[i];

        _categoryRows = rows;
        _displayToSource = [];
        _trackedCategory = data->CrystallizeCategory;
        _emptyCategoryRefreshPasses = 0;
        _deferCategoryRestore = false;
        LogCategoryCaptureOnce(data, rows.Length);
        return true;
    }

    // zero-filter path: commit empty snapshot once native agent and atk tree both agree category is empty
    private unsafe void TryCommitEmptyCategoryIfNativeSettled(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (data->CrystallizeCategory != _trackedCategory)
            return;

        var reported = (int)data->CrystallizeItemCount;
        var scanned = ScanPopulatedCategoryItemCount(data);
        if (reported != 0 || scanned != 0) {
            _emptyCategoryRefreshPasses = 0;
            return;
        }

        _nativeTree.Resolve(addon);
        _nativeTree.CaptureAtkSnapshot(addon);
        if (addon->AtkValuesCount > 0)
            _nativeTree.ParseLayout(0, force: true);

        if (_nativeTree.NativeSlotCount > 0) {
            _emptyCategoryRefreshPasses = 0;
            return;
        }

        _emptyCategoryRefreshPasses++;
        if (_emptyCategoryRefreshPasses < 2)
            return;

        _categoryRows = [];
        _displayToSource = [];
        _trackedCategory = data->CrystallizeCategory;
        _emptyCategoryRefreshPasses = 0;
    }

    private unsafe void EnsureCategoryTracked(MiragePrismPrismBoxData* data) {
        var category = data->CrystallizeCategory;
        if (category == _trackedCategory)
            return;

        _trackedCategory = category;
        ClearTransientState();
        _snapshotCapturePasses = 0;
        ClearFilterLogSignatures();
        _nativeTree.InvalidateAll();
        _needsUnfilteredRepopulate = false;
        _populationWasIncomplete = false;
    }

    // category already changed while agent still holds the previous tab's projected rows
    private unsafe bool TryHandlePreRefreshCategoryChange(MiragePrismPrismBoxData* data) {
        var category = data->CrystallizeCategory;
        if (category == _trackedCategory)
            return false;

        EnsureCategoryTracked(data);
        ClearAgentCategoryItems(data);
        LogFilterDebug(nameof(TryHandlePreRefreshCategoryChange), $"cleared projected rows for category switch to {category}");
        return true;
    }

    private unsafe bool TryDetectCategoryDrift(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (data->CrystallizeCategory == _trackedCategory)
            return false;

        _nativeTree.InvalidateAll();
        EnsureCategoryTracked(data);
        ClearAgentCategoryItems(data);
        RequestAddonRefresh(addon);
        return true;
    }

    private static unsafe void ClearAgentCategoryItems(MiragePrismPrismBoxData* data) {
        data->CrystallizeItemCount = 0;
        data->CrystallizeItemIndex = 0;
        data->CrystallizeTreeRowCount = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private unsafe bool TryApplyFilterPipeline(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        RebuildFilterMap();

        if (_displayToSource.Length > 0) {
            ProjectVisibleRows(data);
            LogFilterApplySummary(data, _categoryRows.Length, _displayToSource.Length);
        }
        else {
            ApplyEmptyCategory(data);
            LogFilterApplySummary(data, _categoryRows.Length, 0);
        }

        return TryRepopulateTree(addon, data, isFilteringActive: true);
    }

    private unsafe bool TryEnsureBaselineFromNative(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_refreshRecursionDepth > 1)
            return false;

        if (_nativeTree.IsBaselineValidFor(data->CrystallizeCategory, _categoryRows.Length) && !_deferCategoryRestore)
            return true;

        _nativeTree.Resolve(addon);
        if (_nativeTree.TreeList is null)
            return false;

        _nativeTree.CaptureAtkSnapshot(addon);
        if (!_nativeTree.HasBufferLayout)
            return false;

        _nativeTree.TrimCapturedSnapshot(_categoryRows.Length);
        _nativeTree.ParseLayout(_categoryRows.Length, force: true);
        if (!_nativeTree.HasLayout || _nativeTree.NativeSlotCount <= 0)
            return false;

        return _nativeTree.TryCommitBaseline(data->CrystallizeCategory, _categoryRows.Length);
    }

    private unsafe bool TryRepopulateTree(AtkUnitBase* addon, MiragePrismPrismBoxData* data, bool isFilteringActive) {
        _nativeTree.Resolve(addon);
        if (_nativeTree.TreeList is null) {
            LogFilterWarning($"tree repopulate aborted: unresolved tree for category {data->CrystallizeCategory}");
            return false;
        }

        _nativeTree.CaptureAtkSnapshot(addon);
        if (!_nativeTree.HasBufferLayout) {
            LogFilterWarning($"tree repopulate aborted: missing buffer layout for category {data->CrystallizeCategory}");
            return false;
        }

        if (!_nativeTree.HasBaseline) {
            LogFilterWarning($"tree repopulate aborted: missing baseline for category {data->CrystallizeCategory} (rows={_categoryRows.Length})");
            return false;
        }

        if (isFilteringActive) {
            var visibleSources = new HashSet<int>(_displayToSource);
            _nativeTree.RepopulateFiltered(
                addon,
                true,
                _displayToSource.Length,
                visibleSources,
                BuildShouldHideLeafPredicate(visibleSources),
                _ => false);
        }
        else {
            var fullSources = new HashSet<int>(Enumerable.Range(0, _categoryRows.Length));
            _nativeTree.RepopulateFiltered(addon, false, _categoryRows.Length, fullSources, _ => false, _ => false);
        }

        // zero-visible filter hides tree lists; don't undo that here
        if (!isFilteringActive || _displayToSource.Length > 0)
            _nativeTree.EnsureAllTreeListsVisible(addon);

        return true;
    }

    private unsafe void RebuildFilterMap() {
        _displayToSource = [];
        if (_categoryRows.Length == 0)
            return;

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

        ClampCrystallizeSelection(data, selectedItemId);
    }

    private unsafe void RestoreFullCategory(MiragePrismPrismBoxData* data) {
        var count = _categoryRows.Length;
        for (var i = 0; i < count; i++)
            data->CrystallizeItems[i] = _categoryRows[i];
        data->CrystallizeItemCount = (ushort)count;
        for (var i = count; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private unsafe void PrepareForNativeRefresh(MiragePrismPrismBoxData* data) {
        if (_deferCategoryRestore)
            return;

        if (_categoryRows.Length > 0 && data->CrystallizeCategory == _trackedCategory)
            RestoreFullCategory(data); // undo projection so native refresh sees full list
    }

    private unsafe void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        data->CrystallizeItemCount = 0;
        data->CrystallizeItemIndex = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
        _displayToSource = [];
    }

    private void ClearTransientState() {
        _categoryRows = [];
        _displayToSource = [];
        _emptyCategoryRefreshPasses = 0;
    }

    private bool ShouldExcludeLeaf(uint itemId) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        return _filters.Any(f => f.IsEnabled && f.ShouldHide(baseId));
    }

    private Func<int, bool> BuildShouldHideLeafPredicate(HashSet<int> visibleSources) {
        var seenSources = new HashSet<int>();
        return sourceIndex => !visibleSources.Contains(sourceIndex) || !seenSources.Add(sourceIndex);
    }

    private unsafe bool TryDetectFilterFlagsChange(MiragePrismPrismBoxData* data) {
        if (_filterFlagsSnapshot == byte.MaxValue) {
            _filterFlagsSnapshot = data->CrystallizeFilterFlags;
            return false;
        }

        if (data->CrystallizeFilterFlags == _filterFlagsSnapshot)
            return false;

        _filterFlagsSnapshot = data->CrystallizeFilterFlags;
        LogFilterDebug(nameof(TryDetectFilterFlagsChange), $"filter flags changed to 0x{data->CrystallizeFilterFlags:X2}");
        return true;
    }

    private unsafe void RequestAddonRefresh(AtkUnitBase* addon) {
        if (_refreshRecursionDepth > 1 || _refreshScheduled)
            return;

        _refreshScheduled = true;
        Svc.Framework.RunOnFrameworkThread(() => {
            _refreshScheduled = false;
            var current = GetAddon();
            if (current is null || !current->IsVisible)
                return;
            current->OnRefresh(0, null);
        });
    }

    private static unsafe void ClearStaleCrystallizeTail(MiragePrismPrismBoxData* data) {
        var reported = data->CrystallizeItemCount;
        for (var i = reported; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
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

    private unsafe void OnFinalize(AtkUnitBase* addon) {
        if (addon is not null)
            _nativeTree.EnsureAllTreeListsVisible(addon);

        _nativeTree.InvalidateAll();
        ClearFilterState();
    }

    private void ClearFilterState() {
        ClearTransientState();
        _trackedCategory = int.MinValue;
        _snapshotCapturePasses = 0;
        _deferCategoryRestore = false;
        _needsUnfilteredRepopulate = false;
        _populationWasIncomplete = false;
        _filterFlagsSnapshot = byte.MaxValue;
        ClearFilterLogSignatures();
        _nativeTree.InvalidateAll();
    }

    private static unsafe AtkUnitBase* GetAddon()
        => Svc.GameGui.GetAddonByName<AtkUnitBase>(CrystallizeNativeTree.AddonName);

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }

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

    public async ValueTask DisposeAsync() {
        if (_disposed)
            return;

        _disposed = true;
        await Svc.Framework.RunOnFrameworkThread(() => {
            _addonController.Dispose();
            _nativeTree.Dispose();
            ClearFilterState();
        });
    }
}
