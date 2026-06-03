using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;

namespace GlamourLog.Features.PrismBox;

internal sealed unsafe class CrystallizeListHandler : IDisposable {
    private const string AddonName = "MiragePrismPrismBoxCrystallize";
    private const int MaxCategoryItems = 140;
    private const uint ItemTreeListNodeId = 11;
    private const uint ListRefreshEventId = 1962;
    private const int InventoryCategory = 0;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly AddonController<AtkUnitBase> _addonController;
    private readonly Hook<AtkComponentTreeList.Delegates.LoadAtkValues>? _loadAtkValuesHook;

    private PrismBoxCrystallizeItem[] _categoryRows = [];
    private int[] _displayToSource = [];
    private int _categoryItemCount;
    private int _nativeCategoryItemCount;
    private int _crystallizeCategory = int.MinValue;
    private bool _needsCategorySnapshot;
    private bool _suppressPostRefreshFilter;
    private bool _pendingInventoryAtkRetry;
    private int _inventoryAtkRetryAttempts;

    private AtkValue[] _pristineAtkValues = [];
    private CrystallizeAtkSlot[] _atkLayout = [];
    private int[] _atkSlotRemap = [];
    private int _nativeAtkSlotCount;
    private int _nativeLoadAtkSlotCount;
    private int _filteredAtkSlotCount;

    public CrystallizeListHandler() {
        _filters = [
            new HideDresserDepositedFilter(),
            new HideArmoireEligibleFilter(),
            new HideNonOutfitItemsFilter(),
        ];

        _loadAtkValuesHook = Svc.Hook.HookFromAddress<AtkComponentTreeList.Delegates.LoadAtkValues>((nint)AtkComponentTreeList.MemberFunctionPointers.LoadAtkValues, LoadAtkValuesDetour);
        _loadAtkValuesHook.Enable();

        _addonController = new AddonController<AtkUnitBase> {
            AddonName = AddonName,
            OnRefresh = OnPostRefresh,
            OnFinalize = OnFinalize,
        };
        _addonController.Enable();

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreRefresh, AddonName, OnLifecyclePreRefresh);
    }

    public void Dispose() {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreRefresh, AddonName, OnLifecyclePreRefresh);
        _addonController.Dispose();
        _loadAtkValuesHook?.Dispose();
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

            _displayToSource = [];
            _categoryItemCount = 0;
            return;
        }

        if (!HasValidCategorySnapshot(data) && !TryCaptureCategorySnapshot(data))
            return;

        TryApplyFilterPipeline(addon, data);
    }

    private void OnLifecyclePreRefresh(AddonEvent type, AddonArgs args) {
        _ = type;
        if (args is AddonRefreshArgs refresh && refresh.AtkValues != 0 && refresh.AtkValueCount > 0)
            CachePristineAtk((AtkValue*)refresh.AtkValues, (int)refresh.AtkValueCount);

        var data = GetData();
        if (data is not null)
            OnCategoryChanging(data);

        if (!_suppressPostRefreshFilter && IsFilteringActive && HasValidCategorySnapshot(data))
            ProjectAgentBeforeRefresh(data);
    }

    private void OnPostRefresh(AtkUnitBase* addon) {
        if (addon is null || _suppressPostRefreshFilter)
            return;

        var data = GetData();
        if (data is null)
            return;

        CachePristineAtk(addon->AtkValues, addon->AtkValuesCount, force: true);
        OnCategoryChanging(data);
        EnsureCategoryTracked(data);

        if (!IsFilteringActive)
            return;

        if (ShouldRecaptureCategory(data)) {
            CaptureCategorySnapshot(data);
            ParseAtkLayout(force: true);
            if (_crystallizeCategory == InventoryCategory)
                BuildInventoryAtkLayout();
        }

        if (!HasValidCategorySnapshot(data))
            return;

        if (_pendingInventoryAtkRetry || IsAtkLayoutStale() || _crystallizeCategory == InventoryCategory && !IsInventoryAtkReady()) {
            RetryAfterStaleAtk();
            return;
        }

        ApplyFilterAfterNativeRefresh(addon, data);
    }

    private void OnFinalize(AtkUnitBase* addon) {
        _ = addon;
        ClearFilterState();
    }

    private void ApplyFilterAfterNativeRefresh(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (!HasValidCategorySnapshot(data))
            return;

        EnsureAtkCached(addon);
        TryApplyFilterPipeline(addon, data);
    }

    private bool TryApplyFilterPipeline(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        if (_categoryRows.Length == 0)
            return false;

        EnsureAtkCached(addon);
        RebuildFilterMap();

        if (_displayToSource.Length == 0) {
            ApplyEmptyCategory(data);
            RepopulateTreeList(addon);
            ApplyFilteredListLayout(addon);
            return true;
        }

        ProjectVisibleRows(data);
        RepopulateTreeList(addon);
        ApplyFilteredListLayout(addon);
        return true;
    }

    private void ApplyEmptyCategory(MiragePrismPrismBoxData* data) {
        _displayToSource = [];
        _categoryItemCount = 0;
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

        _categoryItemCount = count;
        _displayToSource = [.. Enumerable.Range(0, count)];
    }

    private void ProjectAgentBeforeRefresh(MiragePrismPrismBoxData* data) {
        if (_needsCategorySnapshot || _categoryRows.Length == 0)
            return;

        RebuildFilterMap();
        if (_displayToSource.Length == 0)
            return;

        ProjectVisibleRows(data);
    }

    private bool HasValidCategorySnapshot(MiragePrismPrismBoxData* data)
        => data is not null
           && !_needsCategorySnapshot
           && data->CrystallizeCategory == _crystallizeCategory
           && _categoryRows.Length > 0;

    private void RebuildFilterMap() {
        if (_categoryRows.Length == 0) {
            _displayToSource = [];
            _categoryItemCount = 0;
            _filteredAtkSlotCount = 0;
            return;
        }

        var visible = new List<int>(_categoryRows.Length);
        for (var i = 0; i < _categoryRows.Length; i++) {
            if (!ShouldExcludeItem(_categoryRows[i].ItemId))
                visible.Add(i);
        }

        _displayToSource = [.. visible];
        _categoryItemCount = _displayToSource.Length;
        UpdateFilteredAtkSlotCount();
    }

    private bool ShouldExcludeItem(uint itemId) => itemId == 0 || _filters.Any(f => f.IsEnabled && f.ShouldHide(ItemUtil.GetBaseId(itemId).ItemId));
    private bool ShouldExcludeSourceIndex(int sourceIndex) => (uint)sourceIndex >= (uint)_categoryRows.Length || ShouldExcludeItem(_categoryRows[sourceIndex].ItemId);
    private Func<int, bool> BuildAtkHidePredicate() => _displayToSource.Length > 0 ? src => !DisplayMapContains(src) : ShouldExcludeSourceIndex;
    private bool DisplayMapContains(int sourceIndex) => _displayToSource.Any(src => src == sourceIndex);

    private void RetryAfterStaleAtk() {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName);
        var data = GetData();
        if (addon is null || data is null || !IsFilteringActive || !HasValidCategorySnapshot(data))
            return;

        CachePristineAtk(addon->AtkValues, addon->AtkValuesCount, force: true);
        if (_pristineAtkValues.Length == 0)
            return;

        ParseAtkLayout(force: true);
        if (_crystallizeCategory == InventoryCategory)
            BuildInventoryAtkLayout();

        if (_crystallizeCategory == InventoryCategory && !IsInventoryAtkReady()) {
            if (_inventoryAtkRetryAttempts++ < 8) {
                Svc.Framework.RunOnFrameworkThread(RetryAfterStaleAtk);
                return;
            }

            return;
        }

        _inventoryAtkRetryAttempts = 0;
        _pendingInventoryAtkRetry = false;

        if (IsAtkLayoutStale())
            return;

        ApplyFilterAfterNativeRefresh(addon, data);
    }

    private void OnCategoryChanging(MiragePrismPrismBoxData* data) {
        if (data->CrystallizeCategory == _crystallizeCategory && !_needsCategorySnapshot)
            return;

        _pendingInventoryAtkRetry = data->CrystallizeCategory == InventoryCategory
            && _crystallizeCategory != int.MinValue
            && _crystallizeCategory != InventoryCategory;
        _needsCategorySnapshot = true;
        _categoryRows = [];
        _displayToSource = [];
        _filteredAtkSlotCount = 0;
        _nativeLoadAtkSlotCount = 0;
        _atkLayout = [];
        _atkSlotRemap = [];
        _pristineAtkValues = [];
    }

    private void EnsureAtkCached(AtkUnitBase* addon) {
        if (_pristineAtkValues.Length == 0 && addon is not null)
            CachePristineAtk(addon->AtkValues, addon->AtkValuesCount, force: true);

        if (_pristineAtkValues.Length == 0)
            return;

        if (_atkLayout.Length == 0 || IsAtkLayoutStale())
            ParseAtkLayout();

        if (_crystallizeCategory == InventoryCategory)
            BuildInventoryAtkLayout();
    }

    private void ParseAtkLayout(bool force = false) {
        if (_pristineAtkValues.Length == 0) {
            _atkLayout = [];
            _atkSlotRemap = [];
            _nativeAtkSlotCount = 0;
            return;
        }

        if (_crystallizeCategory == InventoryCategory) {
            if (force || _atkLayout.Length == 0)
                BuildInventoryAtkLayout();
            return;
        }

        _atkSlotRemap = [];
        var inferred = CrystallizeListAtk.InferItemCount(_pristineAtkValues);
        var cap = _categoryRows.Length + 3;
        _nativeAtkSlotCount = Math.Min(inferred, cap);
        if (_nativeLoadAtkSlotCount > 0)
            _nativeAtkSlotCount = Math.Min(_nativeAtkSlotCount, _nativeLoadAtkSlotCount);

        _atkLayout = CrystallizeListAtk.Parse(_pristineAtkValues, _nativeAtkSlotCount, _categoryRows.Length);
    }

    private void BuildInventoryAtkLayout() {
        _atkLayout = [];
        _atkSlotRemap = [];

        if (!IsInventoryAtkReady())
            return;

        var scanSlots = _nativeLoadAtkSlotCount > 0
            ? Math.Max(_nativeLoadAtkSlotCount, _categoryRows.Length)
            : _categoryRows.Length;

        var full = CrystallizeListAtk.Parse(_pristineAtkValues, scanSlots, _categoryRows.Length);
        var leaves = new List<(int AtkSlot, int Source)>();
        for (var slot = 0; slot < full.Length; slot++) {
            ref readonly var e = ref full[slot];
            if (e.IsLeaf && e.SourceIndex >= 0 && e.SourceIndex < _categoryRows.Length)
                leaves.Add((slot, e.SourceIndex));
        }

        if (leaves.Count == 0)
            return;

        leaves.Sort((a, b) => a.Source.CompareTo(b.Source));
        _atkLayout = new CrystallizeAtkSlot[leaves.Count];
        _atkSlotRemap = new int[leaves.Count];
        for (var i = 0; i < leaves.Count; i++) {
            _atkLayout[i] = new CrystallizeAtkSlot {
                IsLeaf = true,
                ItemType = AtkComponentTreeListItemType.Leaf,
                SourceIndex = leaves[i].Source,
            };
            _atkSlotRemap[i] = leaves[i].AtkSlot;
        }

        _nativeAtkSlotCount = scanSlots;
    }

    private bool IsAtkLayoutStale() {
        if (_atkLayout.Length == 0 || _categoryRows.Length == 0)
            return false;

        var headers = 0;
        foreach (var slot in _atkLayout) {
            if (CrystallizeListAtk.IsRealHeader(slot))
                headers++;
            else if (slot.IsLeaf && slot.SourceIndex >= _categoryRows.Length)
                return true;
        }

        if (_crystallizeCategory == InventoryCategory && headers > 0)
            return true;

        var maxHeaders = _crystallizeCategory == InventoryCategory ? 0 : 3;
        return headers > maxHeaders || _atkLayout.Length > _categoryRows.Length + maxHeaders + 2;
    }

    private bool IsInventoryAtkReady() {
        if (_pristineAtkValues.Length == 0)
            return false;

        var probe = Math.Min(8, CrystallizeListAtk.InferItemCount(_pristineAtkValues));
        if (probe <= 0)
            return _categoryRows.Length == 0;

        var layout = CrystallizeListAtk.Parse(_pristineAtkValues, probe, _categoryRows.Length);
        return !layout.Any(CrystallizeListAtk.IsRealHeader);
    }

    private void CachePristineAtk(AtkValue* source, int count, bool force = false) {
        if (source is null || count <= 0)
            return;

        if (!force && (IsFilteringActive || _suppressPostRefreshFilter) && !_needsCategorySnapshot)
            return;

        var copy = new AtkValue[count];
        for (var i = 0; i < count; i++)
            copy[i] = source[i];

        _pristineAtkValues = copy;
    }

    private void RepopulateTreeList(AtkUnitBase* addon) {
        if (addon is null)
            return;

        var refreshAtk = _pristineAtkValues.Length == 0
            ? []
            : CrystallizeListAtk.Clone(_pristineAtkValues);

        if (refreshAtk.Length == 0) {
            ApplyFilteredListLayout(addon);
            return;
        }

        _suppressPostRefreshFilter = true;
        try {
            fixed (AtkValue* atkValues = refreshAtk)
                addon->OnRefresh(ListRefreshEventId, atkValues);
        }
        finally {
            _suppressPostRefreshFilter = false;
        }
    }

    private void ApplyFilteredListLayout(AtkUnitBase* addon) {
        var tree = GetItemTreeList(addon);
        if (tree is null)
            return;

        var count = (short)(_filteredAtkSlotCount > 0 ? _filteredAtkSlotCount : _categoryItemCount);
        tree->SetItemCount(0);
        tree->SetItemCount(count);

        if (count > 0) {
            tree->UpdateListItems();
            tree->RecalculateVisibleItems(true);
        }

        tree->LayoutRefreshPending = true;
        tree->IsUpdatePending = true;
        tree->IsScrollRefreshPending = true;
    }

    private void UpdateFilteredAtkSlotCount() {
        if (_displayToSource.Length == 0) {
            _filteredAtkSlotCount = 0;
            return;
        }

        if (_atkLayout.Length == 0) {
            _filteredAtkSlotCount = _categoryItemCount;
            return;
        }

        _filteredAtkSlotCount = CrystallizeListAtk.CountVisibleSlots(
            _atkLayout,
            BuildAtkHidePredicate(),
            IncludeSectionHeaders);
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
        if (IsFilteringActive && GetItemTreeList(Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName)) == thisPtr && _atkLayout.Length > 0) {
            if (itemCount > 0)
                _nativeLoadAtkSlotCount = itemCount;

            var working = new AtkValue[atkValuesCount];
            for (var i = 0; i < atkValuesCount; i++)
                working[i] = atkValues[i];

            var filteredCount = CrystallizeListAtk.ApplyToBuffer(
                working,
                _atkLayout,
                BuildAtkHidePredicate(),
                uintValuesOffset,
                stringValuesOffset,
                uintValuesCountPerItem,
                stringValuesCountPerItem,
                itemCount,
                IncludeSectionHeaders,
                _atkSlotRemap);

            for (var i = 0; i < atkValuesCount; i++)
                atkValues[i] = working[i];

            if (filteredCount > 0)
                _filteredAtkSlotCount = filteredCount;
            else if (_filteredAtkSlotCount <= 0)
                _filteredAtkSlotCount = _categoryItemCount;

            if (itemCount != filteredCount)
                itemCount = filteredCount;
        }

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

    private static AtkComponentTreeList* GetItemTreeList(AtkUnitBase* addon) {
        var node = addon->GetNodeById(ItemTreeListNodeId);
        return node is null ? null : node->GetAsAtkComponentTreeList();
    }

    private bool TryCaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        EnsureCategoryTracked(data);
        if (InferCategoryItemCount(data) <= 0)
            return false;

        CaptureCategorySnapshot(data);
        return true;
    }

    private bool ShouldRecaptureCategory(MiragePrismPrismBoxData* data) {
        if (_needsCategorySnapshot || _categoryRows.Length == 0)
            return InferCategoryItemCount(data) > 0;

        return _crystallizeCategory != data->CrystallizeCategory || InferCategoryItemCount(data) != _categoryRows.Length;
    }

    private void EnsureCategoryTracked(MiragePrismPrismBoxData* data) {
        if (_crystallizeCategory == data->CrystallizeCategory && _nativeCategoryItemCount > 0)
            return;

        _crystallizeCategory = data->CrystallizeCategory;
        _nativeCategoryItemCount = InferCategoryItemCount(data);
        _needsCategorySnapshot = true;
    }

    private void CaptureCategorySnapshot(MiragePrismPrismBoxData* data) {
        var count = _nativeCategoryItemCount > 0 ? _nativeCategoryItemCount : InferCategoryItemCount(data);
        if (count <= 0) {
            _categoryRows = [];
            _displayToSource = [];
            _needsCategorySnapshot = false;
            return;
        }

        _nativeCategoryItemCount = count;
        _categoryRows = new PrismBoxCrystallizeItem[count];
        for (var i = 0; i < count; i++)
            _categoryRows[i] = data->CrystallizeItems[i];

        _crystallizeCategory = data->CrystallizeCategory;
        _needsCategorySnapshot = false;
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

    private void ClearFilterState() {
        _categoryRows = [];
        _displayToSource = [];
        _nativeCategoryItemCount = 0;
        _categoryItemCount = 0;
        _crystallizeCategory = int.MinValue;
        _needsCategorySnapshot = false;
        _pendingInventoryAtkRetry = false;
        _inventoryAtkRetryAttempts = 0;
        _pristineAtkValues = [];
        _atkLayout = [];
        _atkSlotRemap = [];
        _nativeAtkSlotCount = 0;
        _nativeLoadAtkSlotCount = 0;
        _filteredAtkSlotCount = 0;
    }
}
