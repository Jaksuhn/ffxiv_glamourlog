using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed class CrystallizeListHandler : IAsyncDisposable {
    private const int MaxCategoryItems = 140;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly CrystallizeNativeTree _nativeTree;
    private readonly AddonController<AtkUnitBase> _addonController;

    private PrismBoxCrystallizeItem[] _categoryRows = [];
    private int[] _displayToSource = [];
    private int _snapshotCategory = int.MinValue;
    private bool _needsSnapshot;
    private bool _disposed;
    private byte _filterFlagsSnapshot = byte.MaxValue;
    private int _refreshRecursionDepth;
    private bool _preserveCategoryRowsForRefresh;

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
            OnFinalize = OnFinalize,
        };
        _addonController.Enable();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    internal void OnConfigChanged() => Svc.Framework.RunOnFrameworkThread(ApplyConfigChange);

    private unsafe void ApplyConfigChange() {
        var addon = GetAddon();
        var data = GetData();
        if (addon is null || data is null) {
            ClearFilterState();
            return;
        }

        if (!IsFilteringActive) {
            RestoreFullCategory(data);
            _displayToSource = [];
            Svc.Log.Information($"[PrismBox] filters disabled; restored full category {data->CrystallizeCategory}");
            addon->OnRefresh(0, null);
            return;
        }

        _needsSnapshot = true;
        _nativeTree.InvalidateAtkCache();
        ResetSnapshotState();

        if (_categoryRows.Length > 0 && data->CrystallizeCategory == _snapshotCategory)
            RestoreFullCategory(data);

        if (!TryCaptureCategorySnapshot(data))
            addon->OnRefresh(0, null);
        else
            TryApplyFilterPipeline(addon, data);
    }

    private unsafe void OnPreRefresh(AtkUnitBase* addon) {
        var data = GetData();
        if (data is null)
            return;

        if (TryDetectFilterFlagsChange(data)) {
            _needsSnapshot = true;
            _nativeTree.InvalidateAtkCache();
            Svc.Log.Information($"[PrismBox] filter flags changed; restoring full category before refresh");
        }

        if (_categoryRows.Length > 0 && data->CrystallizeCategory == _snapshotCategory)
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
                if (_categoryRows.Length > 0 && data->CrystallizeCategory == _snapshotCategory)
                    RestoreFullCategory(data);
                _nativeTree.Resolve(addon);
                _nativeTree.CaptureAtkSnapshot(addon);
                if (_categoryRows.Length > 0 && _nativeTree.HasBufferLayout && _nativeTree.HasSnapshot)
                    _nativeTree.ParseLayout(_categoryRows.Length, force: true);
                _nativeTree.EnsureVisible(addon);
                return;
            }

            ApplyFilterAfterRefresh(addon, data);
        }
        finally {
            _refreshRecursionDepth--;
        }
    }

    private unsafe void ApplyFilterAfterRefresh(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (TryDetectFilterFlagsChange(data)) {
            ResetSnapshotState(preserveCategoryRows: true);
            Svc.Log.Information($"[PrismBox] preserving snapshot while refreshing after filter toggle");
        }

        if (_needsSnapshot && !TryCaptureCategorySnapshot(data)) {
            RequestAddonRefresh(addon);
            return;
        }

        if (_categoryRows.Length == 0) {
            ApplyEmptyCategory(data);
            Svc.Log.Information($"[PrismBox] empty category snapshot for {data->CrystallizeCategory}");
            _nativeTree.Resolve(addon);
            _nativeTree.CaptureAtkSnapshot(addon);
            if (_nativeTree.HasBufferLayout)
                _nativeTree.ParseLayout(0, force: true);
            _nativeTree.RepopulateFiltered(addon, false, 0, [], _ => true, _ => false);
            _nativeTree.EnsureVisible(addon, hideOtherTreeLists: true);
            return;
        }

        if (!TryApplyFilterPipeline(addon, data)) {
            Svc.Log.Warning($"[PrismBox] filter pipeline stalled for category {data->CrystallizeCategory}");
            RequestAddonRefresh(addon);
        }
    }

    private unsafe bool TryCaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        if (_preserveCategoryRowsForRefresh && _categoryRows.Length > 0 && data->CrystallizeCategory == _snapshotCategory) {
            _displayToSource = [];
            _needsSnapshot = false;
            _preserveCategoryRowsForRefresh = false;
            Svc.Log.Information($"[PrismBox] reusing existing full snapshot for category {data->CrystallizeCategory}");
            return true;
        }

        if (IsFilteringActive && _categoryRows.Length > 0 && data->CrystallizeCategory == _snapshotCategory) {
            var currentCount = (int)data->CrystallizeItemCount;
            if (currentCount > 0 && currentCount < _categoryRows.Length) {
                _displayToSource = [];
                _needsSnapshot = false;
                _preserveCategoryRowsForRefresh = false;
                Svc.Log.Information($"[PrismBox] keeping full snapshot for category {data->CrystallizeCategory} (current count {currentCount}, snapshot { _categoryRows.Length})");
                return true;
            }
        }

        _preserveCategoryRowsForRefresh = false;
        ClearStaleCrystallizeTail(data);

        var reported = (int)data->CrystallizeItemCount;
        var scanned = ScanPopulatedCategoryItemCount(data);
        var count = Math.Max(reported, scanned);

        if (count == 0) {
            _categoryRows = [];
            _displayToSource = [];
            _needsSnapshot = false;
            _snapshotCategory = data->CrystallizeCategory;
            return true;
        }

        for (var i = 0; i < count; i++) {
            if (data->CrystallizeItems[i].ItemId == 0)
                return false;
        }

        var rows = new PrismBoxCrystallizeItem[count];
        for (var i = 0; i < count; i++)
            rows[i] = data->CrystallizeItems[i];

        _categoryRows = rows;
        _displayToSource = [];
        _needsSnapshot = false;
        _snapshotCategory = data->CrystallizeCategory;
        Svc.Log.Information($"[PrismBox] captured {rows.Length} rows for category {data->CrystallizeCategory} (flags=0x{data->CrystallizeFilterFlags:X2})");
        return true;
    }

    private unsafe bool TryApplyFilterPipeline(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _nativeTree.Resolve(addon);
        if (_nativeTree.TreeList is null)
            return false;

        RebuildFilterMap();
        if (_displayToSource.Length == 0) {
            ApplyEmptyCategory(data);
            Svc.Log.Information($"[PrismBox] no visible rows after filtering category {data->CrystallizeCategory}");
            return true;
        }

        ProjectVisibleRows(data);
        LogFilterSummary("apply", data, _categoryRows.Length, _displayToSource.Length);

        _nativeTree.CaptureAtkSnapshot(addon);
        if (!_nativeTree.HasBufferLayout)
            return false;

        _nativeTree.ParseLayout(_categoryRows.Length, force: true);
        if (!_nativeTree.HasLayout)
            return false;

        RepopulateDisplayTree(addon);
        _nativeTree.EnsureVisible(addon, hideOtherTreeLists: true);
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
        LogFilterSummary("rebuild", null, _categoryRows.Length, _displayToSource.Length);
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
        _displayToSource = [.. Enumerable.Range(0, _categoryRows.Length)];
    }

    private unsafe void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        data->CrystallizeItemCount = 0;
        data->CrystallizeItemIndex = 0;
        for (var i = 0; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
        _displayToSource = [];
    }

    private void ResetSnapshotState(bool preserveCategoryRows = false) {
        if (!preserveCategoryRows) {
            _categoryRows = [];
            _displayToSource = [];
            _snapshotCategory = int.MinValue;
        } else {
            _displayToSource = [];
        }

        _preserveCategoryRowsForRefresh = preserveCategoryRows;
        _needsSnapshot = true;
    }

    private bool ShouldExcludeLeaf(uint itemId) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        return _filters.Any(f => f.IsEnabled && f.ShouldHide(baseId));
    }

    private unsafe void RepopulateDisplayTree(AtkUnitBase* addon) {
        var visibleSources = new HashSet<int>(_displayToSource);
        _nativeTree.RepopulateFiltered(addon, IsFilteringActive, _displayToSource.Length, visibleSources, BuildShouldHideLeafPredicate(visibleSources), _ => false);
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
        Svc.Log.Information($"[PrismBox] filter flags changed to 0x{data->CrystallizeFilterFlags:X2}");
        return true;
    }

    private unsafe void RequestAddonRefresh(AtkUnitBase* addon) {
        if (_refreshRecursionDepth > 1)
            return;
        addon->OnRefresh(0, null);
    }

    private static unsafe void ClearStaleCrystallizeTail(MiragePrismPrismBoxData* data) {
        var reported = data->CrystallizeItemCount;
        for (var i = reported; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
    }

    private static unsafe void LogFilterSummary(string phase, MiragePrismPrismBoxData* data, int sourceCount, int visibleCount) {
        if (data is null)
            Svc.Log.Debug($"[PrismBox] {phase}: rows={sourceCount}, visible={visibleCount}");
        else
            Svc.Log.Debug($"[PrismBox] {phase}: category={data->CrystallizeCategory}, flags=0x{data->CrystallizeFilterFlags:X2}, rows={sourceCount}, visible={visibleCount}");
    }

    private static unsafe int ScanPopulatedCategoryItemCount(MiragePrismPrismBoxData* data) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }

        return lastIndex >= 0 ? lastIndex + 1 : 0;
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
        if (addon is not null) {
            _nativeTree.EnsureVisible(addon);
            var data = GetData();
            if (data is not null && _categoryRows.Length > 0)
                RestoreFullCategory(data);
        }

        _nativeTree.InvalidateAll();
        ClearFilterState();
    }

    private void ClearFilterState() {
        _categoryRows = [];
        _displayToSource = [];
        _needsSnapshot = true;
        _snapshotCategory = int.MinValue;
        _preserveCategoryRowsForRefresh = false;
        _filterFlagsSnapshot = byte.MaxValue;
        _nativeTree.InvalidateAll();
    }

    private static unsafe AtkUnitBase* GetAddon()
        => Svc.GameGui.GetAddonByName<AtkUnitBase>(CrystallizeNativeTree.AddonName);

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
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
