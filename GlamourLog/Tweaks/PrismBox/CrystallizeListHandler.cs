using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed partial class CrystallizeListHandler : IAsyncDisposable {
    private bool _disposed;
    private const int MaxCategoryItems = 140;
    private const int PrismBoxItemIdCount = 800;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly CrystallizeNativeTree _nativeTree;
    private readonly AddonController<AtkUnitBase> _addonController;
    private readonly uint[] _prismBoxItemIdsSnapshot = new uint[PrismBoxItemIdCount];

    private bool _prismBoxItemIdsInitialized;
    private byte _crystallizeFilterFlagsSnapshot = byte.MaxValue;
    private PrismBoxCrystallizeItem[] _categoryRows = [];
    private int[] _displayToSource = [];
    private int _categoryItemCount;
    private int _crystallizeCategory = int.MinValue;
    private bool _needsCategorySnapshot;
    private bool _filterFlagsChangedThisRefresh;
    private int _refreshRecursionDepth;

    public unsafe CrystallizeListHandler() {
        _filters = [
            new HideDresserDepositedFilter(),
            new HideArmoireEligibleFilter(),
            new HideNonOutfitItemsFilter(),
        ];
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
                RestoreFullCategory(data);
            _displayToSource = [];
            ClearFilterLogSignatures();
            addon->OnRefresh(0, null);
            return;
        }

        EnsureCategoryTracked(data);

        if (_categoryRows.Length > 0)
            RestoreFullCategory(data);

        // recapture live addon; in-place filtering leaves the tree showing a subset.
        _nativeTree.Resolve(addon);
        _nativeTree.CaptureAtkSnapshot(addon);
        _nativeTree.ParseLayout(_categoryRows.Length, force: true);

        if (HasValidCategorySnapshot(data) && CanApplyFilter() && TryApplyFilterPipeline(addon, data))
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

        _filterFlagsChangedThisRefresh = false;
        _nativeTree.EnsureVisible(addon);
        EnsureCategoryTracked(data);

        if (TryDetectFilterFlagsChange(data)) {
            _nativeTree.InvalidateAtkCache();
            _needsCategorySnapshot = true;
            _categoryItemCount = 0;
        }

        if (_categoryRows.Length > 0 && !_filterFlagsChangedThisRefresh && ShouldRestoreBeforeNativeRefresh(data))
            RestoreFullCategory(data);
    }

    private unsafe void OnPostRefresh(AtkUnitBase* addon) {
        if (addon is null || !addon->IsVisible)
            return;

        var data = GetData();
        if (data is null)
            return;

        _refreshRecursionDepth++;
        try {
            if (!IsFilteringActive) {
                _nativeTree.Resolve(addon);
                _nativeTree.CaptureAtkSnapshot(addon);
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
        _nativeTree.EnsureVisible(addon);
        EnsureCategoryTracked(data);
        TryDetectFilterFlagsChange(data);
        _nativeTree.CaptureAtkSnapshot(addon);

        if (_needsCategorySnapshot || _filterFlagsChangedThisRefresh) {
            if (!CaptureCategorySnapshotAfterNative(data)) {
                LogFilterDebug(nameof(OnPostRefresh), "category snapshot unavailable after native refresh");
                return;
            }
        }
        else {
            _nativeTree.ParseLayout(_categoryRows.Length, force: true);
        }

        if (!CanApplyFilter())
            return;

        if (_nativeTree.IsTruncatedVersus(_categoryRows.Length, InferPopulatedCategoryItemCount(data))) {
            RestoreFullCategory(data);
            RequestAddonRefresh(addon);
            return;
        }

        TryApplyFilterPipeline(addon, data);
        LogFilterOnState(nameof(OnPostRefresh), addon, data);
    }

    private unsafe void OnAddonUpdate(AtkUnitBase* addon) {
        if (addon is null || !addon->IsVisible || !IsFilteringActive)
            return;

        var data = GetData();
        if (data is null)
            return;

        if (TryDetectFilterFlagsChange(data)) {
            ApplyFilterAfterFlagsChange(addon, data);
            return;
        }

        var mirage = MirageManager.Instance();
        if (mirage is null || !TryDetectPrismBoxItemIdsChange(mirage))
            return;

        _needsCategorySnapshot = true;
        _nativeTree.InvalidateAtkCache();
        ClearFilterLogSignatures();
        RequestAddonRefresh(addon);
    }

    private unsafe void OnFinalize(AtkUnitBase* addon) {
        if (addon is not null) {
            _nativeTree.EnsureVisible(addon);
            var data = GetData();
            if (data is not null && _categoryRows.Length > 0)
                RestoreFullCategory(data);
        }
        _nativeTree.InvalidateAll();
        ClearFilterState();
    }

    private unsafe bool TryApplyFilterPipeline(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_categoryRows.Length == 0 || !CanApplyFilter())
            return false;

        _nativeTree.Resolve(addon);
        if (_nativeTree.TreeList is null)
            return false;

        if (!_nativeTree.HasLayout || _nativeTree.NativeSlotCount <= 0)
            _nativeTree.ParseLayout(_categoryRows.Length, force: true);

        if (!_nativeTree.HasLayout || _nativeTree.NativeSlotCount <= 0)
            return false;

        RebuildFilterMap();
        if (_displayToSource.Length == 0) {
            ApplyEmptyCategory(data);
            RepopulateDisplayTree();
            return true;
        }

        ProjectVisibleRows(data);
        RepopulateDisplayTree();
        LogApplyPipelineResult(_nativeTree.TreeList, data);
        return true;
    }

    private void RepopulateDisplayTree() {
        var visibleSources = new HashSet<int>(_displayToSource);
        _nativeTree.RepopulateFiltered(IsFilteringActive, _displayToSource.Length, visibleSources, BuildShouldHideLeafPredicate(visibleSources), ShouldExcludeSourceIndex);
    }

    private bool CanApplyFilter()
        => _nativeTree.HasBufferLayout && _nativeTree.HasSnapshot && _nativeTree.HasLayout && _categoryRows.Length > 0;

    private unsafe void ApplyFilterAfterFlagsChange(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _nativeTree.Resolve(addon);
        _nativeTree.EnsureVisible(addon);
        _needsCategorySnapshot = true;
        _categoryItemCount = 0;
        _nativeTree.CaptureAtkSnapshot(addon);

        if (!CaptureCategorySnapshotAfterNative(data) || !CanApplyFilter()) {
            RequestAddonRefresh(addon);
            return;
        }

        TryApplyFilterPipeline(addon, data);
    }

    private unsafe bool CaptureCategorySnapshotAfterNative(MiragePrismPrismBoxData* data) {
        if (!_nativeTree.HasSnapshot)
            return false;

        _nativeTree.ParseLayout(0, force: true);
        if (!_nativeTree.HasLayout)
            return false;

        var layoutMaxSource = _nativeTree.GetLayoutMaxSourceIndex();
        var scannedCount = InferPopulatedCategoryItemCount(data);
        var rowCount = Math.Max(scannedCount, _nativeTree.ListLength);
        if (_crystallizeFilterFlagsSnapshot == 0)
            rowCount = Math.Max(rowCount, layoutMaxSource);
        if (rowCount <= 0)
            return false;

        _categoryRows = new PrismBoxCrystallizeItem[rowCount];
        var populatedFromAgent = Math.Min(scannedCount, rowCount);
        for (var i = 0; i < populatedFromAgent; i++)
            _categoryRows[i] = data->CrystallizeItems[i];

        _nativeTree.FillMissingRows(_categoryRows, data);
        _categoryItemCount = rowCount;
        _needsCategorySnapshot = false;
        _crystallizeCategory = data->CrystallizeCategory;
        _nativeTree.ParseLayout(_categoryRows.Length, force: true);
        return _categoryRows.Any(row => row.ItemId != 0);
    }

    private unsafe void EnsureCategoryTracked(MiragePrismPrismBoxData* data) {
        if (data->CrystallizeCategory == _crystallizeCategory && _categoryRows.Length > 0)
            return;

        if (data->CrystallizeCategory != _crystallizeCategory) {
            _crystallizeCategory = data->CrystallizeCategory;
            _categoryRows = [];
            _categoryItemCount = 0;
            _nativeTree.InvalidateAtkCache();
            ClearFilterLogSignatures();
        }

        if (_categoryRows.Length > 0)
            return;

        _categoryItemCount = InferCategoryItemCount(data);
        if (_categoryItemCount > 0)
            _needsCategorySnapshot = true;
    }

    private void RebuildFilterMap() {
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
        ClampCrystallizeSelection(data, selectedItemId);
    }

    private unsafe void RestoreFullCategory(MiragePrismPrismBoxData* data) {
        var count = _categoryRows.Length;
        for (var i = 0; i < count; i++)
            data->CrystallizeItems[i] = _categoryRows[i];
        data->CrystallizeItemCount = (ushort)count;
        for (var i = count; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
        _displayToSource = [.. Enumerable.Range(0, count)];
    }

    private unsafe void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        _displayToSource = [];
        data->CrystallizeItemCount = 0;
        data->CrystallizeItemIndex = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private unsafe bool ShouldRestoreBeforeNativeRefresh(MiragePrismPrismBoxData* data) {
        if (_needsCategorySnapshot || _categoryRows.Length == 0)
            return false;
        if (MatchesFilteredProjection(data))
            return true;
        if (AgentMatchesCategorySnapshot(data))
            return false;
        _needsCategorySnapshot = true;
        return false;
    }

    private unsafe bool MatchesFilteredProjection(MiragePrismPrismBoxData* data) {
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

    private unsafe bool AgentMatchesCategorySnapshot(MiragePrismPrismBoxData* data) {
        var agentCount = InferPopulatedCategoryItemCount(data);
        if (agentCount != _categoryRows.Length)
            return false;
        for (var i = 0; i < agentCount; i++) {
            if (data->CrystallizeItems[i].ItemId != _categoryRows[i].ItemId)
                return false;
        }
        return true;
    }

    private bool ShouldExcludeLeaf(uint itemId)
        => _filters.Any(f => f.IsEnabled && f.ShouldHide(ItemUtil.GetBaseId(itemId).ItemId));

    private bool ShouldExcludeSourceIndex(int sourceIndex)
        => (uint)sourceIndex >= (uint)_categoryRows.Length || ShouldExcludeLeaf(_categoryRows[sourceIndex].ItemId);

    private Func<int, bool> BuildShouldHideLeafPredicate(HashSet<int> visibleSources) {
        var seenSources = new HashSet<int>();
        return sourceIndex => {
            if (!visibleSources.Contains(sourceIndex))
                return true;
            if (ShouldExcludeSourceIndex(sourceIndex))
                return true;
            return !seenSources.Add(sourceIndex);
        };
    }

    private unsafe bool HasValidCategorySnapshot(MiragePrismPrismBoxData* data)
        => data->CrystallizeCategory == _crystallizeCategory && _categoryRows.Length > 0;

    private unsafe bool TryDetectFilterFlagsChange(MiragePrismPrismBoxData* data) {
        if (_crystallizeFilterFlagsSnapshot == byte.MaxValue) {
            SyncFilterFlagsSnapshot(data);
            return false;
        }
        if (data->CrystallizeFilterFlags == _crystallizeFilterFlagsSnapshot)
            return false;
        SyncFilterFlagsSnapshot(data);
        _filterFlagsChangedThisRefresh = true;
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
        if (_refreshRecursionDepth > 1)
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

    private static unsafe int InferCategoryItemCount(MiragePrismPrismBoxData* data) {
        var populated = InferPopulatedCategoryItemCount(data);
        return populated > 0 ? populated : data->CrystallizeItemCount > 0 ? data->CrystallizeItemCount : 0;
    }

    private static unsafe int InferPopulatedCategoryItemCount(MiragePrismPrismBoxData* data) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }
        return lastIndex >= 0 ? lastIndex + 1 : 0;
    }

    private void ClearFilterState() {
        _categoryRows = [];
        _displayToSource = [];
        _categoryItemCount = 0;
        _crystallizeCategory = int.MinValue;
        _needsCategorySnapshot = false;
        _prismBoxItemIdsInitialized = false;
        _crystallizeFilterFlagsSnapshot = byte.MaxValue;
        _nativeTree.InvalidateAll();
        ClearFilterLogSignatures();
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
