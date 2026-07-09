using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Services;
using KamiToolKit.Controllers;
using System.Threading.Tasks;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule;

namespace GlamourLog.Features.Cabinet;

internal sealed partial class CabinetListHandler : IAsyncDisposable {
    private bool _disposed;
    private const string AddonName = "Cabinet";
    private const int MaxCategoryItems = 140;

    private readonly ICabinetRowFilter[] _filters;
    private readonly AddonController<AddonCabinet> _addonController;

    private int[] _displayToSource = [];
    private CategoryRowSnapshot[] _categoryRows = [];
    private readonly uint[] _categoryItemIds = new uint[MaxCategoryItems];
    private int _categoryItemCount;
    private uint _categoryIndex = uint.MaxValue;
    private int _pendingFullListCount;
    private int _snapshotCapturePasses;

    public unsafe CabinetListHandler() {
        _filters = [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()];

        _addonController = new AddonController<AddonCabinet> {
            AddonName = AddonName,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnFinalize = OnFinalize,
        };

        _addonController.Enable();
        Svc.Get<OwnershipService>().ArmoireOwnershipChanged += OnArmoireOwnershipChanged;
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool ShouldExcludeItem(uint itemId) => itemId == 0 || _filters.Any(f => f.IsEnabled && f.ShouldHide(itemId));

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(ApplyConfigChange);
    }

    private unsafe void ApplyConfigChange() {
        CabinetGearsetLookup.Invalidate();

        var addon = Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName);
        if (addon is null) {
            ClearFilterState();
            return;
        }

        var agent = AgentCabinet.Instance();

        if (!IsFilteringActive) {
            var fullCount = _categoryItemCount > 0
                ? _categoryItemCount
                : agent is not null ? InferCategoryItemCount(addon, agent) : 0;
            ClearFilterState();
            _pendingFullListCount = fullCount;
            addon->OnRefresh(0, null);
            return;
        }

        if (agent is null)
            return;

        EnsureCategoryTracked(addon);
        addon->OnRefresh(0, null);
    }

    private unsafe void OnArmoireOwnershipChanged() {
        if (!IsFilteringActive)
            return;

        Svc.Framework.RunOnFrameworkThread(() => {
            ClearFilterLogSignatures();
            if (Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName) is not null and var addon)
                addon->OnRefresh(0, null);
        });
    }

    private unsafe void OnPreRefresh(AddonCabinet* addon) {
        if (!IsFilteringActive)
            return;

        CabinetGearsetLookup.Invalidate();

        var agent = AgentCabinet.Instance();
        if (agent is null)
            return;

        EnsureCategoryTracked(addon);

        if (_categoryRows.Length > 0 && _categoryIndex == addon->CategoryIndex)
            RestoreFullCategory(agent, addon);
    }

    private unsafe void OnPostRefresh(AddonCabinet* addon) {
        if (addon is null)
            return;

        if (IsFilteringActive) {
            var agent = AgentCabinet.Instance();
            if (agent is not null)
                ApplyFilterAfterNativeRefresh(addon, agent);

            return;
        }

        if (_pendingFullListCount > 0) {
            ApplyFullListLayout(addon, _pendingFullListCount);
            _pendingFullListCount = 0;
        }

        if (AgentCabinet.Instance() is not null and var filterOffAgent)
            LogFilterOffState(nameof(OnPostRefresh), addon, filterOffAgent);
    }

    private unsafe void OnFinalize(AddonCabinet* addon) {
        ClearFilterState();
    }

    private unsafe void ApplyFilterAfterNativeRefresh(AddonCabinet* addon, AgentCabinet* agent) {
        EnsureCategoryTracked(addon);

        if (!TryCaptureCategorySnapshot(agent, addon)) {
            LogSnapshotUnavailableOnce(addon, agent);
            if (++_snapshotCapturePasses < 8)
                addon->OnRefresh(0, null);
            return;
        }

        if (_categoryRows.Length == 0) {
            ApplyEmptyCategory(agent, addon);
            LogFilterOnState(nameof(OnPostRefresh), addon, agent);
            return;
        }

        TryApplyFilterPipeline(addon, agent);
        LogFilterOnState(nameof(OnPostRefresh), addon, agent);
    }

    private unsafe bool TryApplyFilterPipeline(AddonCabinet* addon, AgentCabinet* agent) {
        if (_categoryRows.Length == 0)
            return false;

        RebuildFilterMap();
        ProjectVisibleRows(agent, addon);
        ApplyFilteredListLayout(addon);
        LogApplyPipelineResult(addon);
        return true;
    }

    private unsafe void EnsureCategoryTracked(AddonCabinet* addon) {
        if (_categoryIndex == addon->CategoryIndex)
            return;

        _categoryIndex = addon->CategoryIndex;
        _categoryItemCount = 0;
        _snapshotCapturePasses = 0;
        ReleaseCategorySnapshot();
        ClearFilterLogSignatures();
    }

    private unsafe bool TryCaptureCategorySnapshot(AgentCabinet* agent, AddonCabinet* addon) {
        if (agent->PendingUpdate)
            return false;

        ClearStaleCabinetTail(agent, addon);

        var reported = InferReportedCategoryItemCount(addon, agent);
        if (reported == 0) {
            CommitCapturedCategory(agent, addon, 0);
            return true;
        }

        for (var i = 0; i < reported; i++) {
            if (agent->ItemCaches[i].Id == 0)
                return false;
        }

        var scanned = ScanPopulatedCategoryItemCount(agent);
        if (scanned > reported)
            return false;

        CommitCapturedCategory(agent, addon, reported);
        return true;
    }

    private unsafe void CommitCapturedCategory(AgentCabinet* agent, AddonCabinet* addon, int count) {
        ReleaseCategorySnapshot();

        _categoryItemCount = count;
        _categoryIndex = addon->CategoryIndex;
        _snapshotCapturePasses = 0;

        if (count == 0) {
            _categoryRows = [];
            return;
        }

        _categoryRows = new CategoryRowSnapshot[count];
        for (var i = 0; i < count; i++) {
            _categoryRows[i] = new CategoryRowSnapshot {
                Slot = ItemSlotProjection.Capture(ref addon->ItemSlots[i]),
                Cache = ItemCacheProjection.Capture(ref agent->ItemCaches[i]),
            };
            _categoryItemIds[i] = _categoryRows[i].Cache.Id != 0
                ? Svc.Get<OwnershipService>().GetItemIdFromLookups(_categoryRows[i].Cache.Id)
                : ResolveRowItemId(agent, addon, i);
        }
    }

    private void ReleaseCategorySnapshot() {
        foreach (var row in _categoryRows)
            row.Dispose();

        _categoryRows = [];
        Array.Clear(_categoryItemIds);
    }

    private void RebuildFilterMap() {
        var visible = new List<int>(_categoryItemCount);
        for (var i = 0; i < _categoryItemCount; i++) {
            if (!ShouldExcludeItem(_categoryItemIds[i]))
                visible.Add(i);
        }

        _displayToSource = [.. visible];
        LogFilterRebuildSummary();
    }

    private unsafe void ProjectVisibleRows(AgentCabinet* agent, AddonCabinet* addon) {
        var visibleCount = _displayToSource.Length;
        for (var displayIndex = 0; displayIndex < visibleCount; displayIndex++) {
            var sourceIndex = _displayToSource[displayIndex];
            if ((uint)sourceIndex >= (uint)_categoryRows.Length)
                continue;

            _categoryRows[sourceIndex].Slot.ApplyTo(ref addon->ItemSlots[displayIndex]);
            _categoryRows[sourceIndex].Cache.ApplyTo(ref agent->ItemCaches[displayIndex]);
        }

        ClearCabinetRowRange(agent, addon, visibleCount, MaxCategoryItems);
    }

    private unsafe void RestoreFullCategory(AgentCabinet* agent, AddonCabinet* addon) {
        var count = _categoryRows.Length;
        for (var i = 0; i < count; i++) {
            _categoryRows[i].Slot.ApplyTo(ref addon->ItemSlots[i]);
            _categoryRows[i].Cache.ApplyTo(ref agent->ItemCaches[i]);
        }

        ClearCabinetRowRange(agent, addon, count, MaxCategoryItems);
        _displayToSource = [.. Enumerable.Range(0, count)];
    }

    private unsafe void ApplyEmptyCategory(AgentCabinet* agent, AddonCabinet* addon) {
        ClearCabinetRowRange(agent, addon, 0, MaxCategoryItems);
        _displayToSource = [];
        ApplyFilteredListLayout(addon);
    }

    private unsafe void ApplyFilteredListLayout(AddonCabinet* addon) {
        var list = addon->ItemList;
        if (list is null)
            return;

        var count = (short)_displayToSource.Length;
        list->SetItemCount(0);
        list->SetItemCount(count);

        if (count > 0) {
            list->UpdateListItems();
            list->RecalculateVisibleItems(true);
        }

        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    private struct CategoryRowSnapshot {
        internal ItemSlotProjection Slot;
        internal ItemCacheProjection Cache;

        internal void Dispose() {
            Slot.Dispose();
            Cache.Dispose();
        }
    }

    private unsafe struct ItemSlotProjection {
        internal uint IconId;
        internal uint InventorySlotIndex;
        internal uint InventoryContainerType;
        internal uint ItemsArrayIndex;
        internal float ConditionNormalized;
        private Utf8String* _nameClone;

        internal static ItemSlotProjection Capture(ref AddonCabinet.ItemSlot source) {
            fixed (Utf8String* name = &source.Name)
                return new ItemSlotProjection {
                    IconId = source.Unk68,
                    InventorySlotIndex = source.InventorySlotIndex,
                    InventoryContainerType = source.InventoryContainerType,
                    ItemsArrayIndex = source.ItemsArrayIndex,
                    ConditionNormalized = source.ConditionNormalized,
                    _nameClone = Utf8String.FromUtf8String(name),
                };
        }

        internal readonly void ApplyTo(ref AddonCabinet.ItemSlot dest) {
            dest.Unk68 = IconId;
            dest.InventorySlotIndex = InventorySlotIndex;
            dest.InventoryContainerType = InventoryContainerType;
            dest.ItemsArrayIndex = ItemsArrayIndex;
            dest.ConditionNormalized = ConditionNormalized;

            dest.Name.Clear();
            if (_nameClone is not null)
                dest.Name.Copy(_nameClone);
        }

        internal void Dispose() {
            if (_nameClone is null)
                return;

            IMemorySpace.Free(_nameClone);
            _nameClone = null;
        }
    }

    private unsafe struct ItemCacheProjection {
        internal uint Id;
        internal uint IconId;
        internal uint StackSize;
        internal byte EquipSlotCategory;
        internal byte AdditionalDataCount;
        internal byte AdditionalData;
        internal byte LevelEquip;
        internal byte SubStatCategory;
        internal short LevelItem;
        internal uint GlamourId;
        private Utf8String* _nameClone;

        internal static ItemCacheProjection Capture(ref ItemCache source) {
            fixed (Utf8String* name = &source.Name)
                return new ItemCacheProjection {
                    Id = source.Id,
                    IconId = source.IconId,
                    StackSize = source.StackSize,
                    EquipSlotCategory = source.EquipSlotCategory,
                    AdditionalDataCount = source.AdditionalDataCount,
                    AdditionalData = source.AdditionalData,
                    LevelEquip = source.LevelEquip,
                    SubStatCategory = source.SubStatCategory,
                    LevelItem = source.LevelItem,
                    GlamourId = source.GlamourId,
                    _nameClone = Utf8String.FromUtf8String(name),
                };
        }

        internal readonly void ApplyTo(ref ItemCache dest) {
            dest.Clear();
            dest.Id = Id;
            dest.IconId = IconId;
            dest.StackSize = StackSize;
            dest.EquipSlotCategory = EquipSlotCategory;
            dest.AdditionalDataCount = AdditionalDataCount;
            dest.AdditionalData = AdditionalData;
            dest.LevelEquip = LevelEquip;
            dest.SubStatCategory = SubStatCategory;
            dest.LevelItem = LevelItem;
            dest.GlamourId = GlamourId;

            if (_nameClone is not null)
                dest.Name.Copy(_nameClone);
        }

        internal void Dispose() {
            if (_nameClone is null)
                return;

            IMemorySpace.Free(_nameClone);
            _nameClone = null;
        }
    }

    private static unsafe uint ResolveRowItemId(AgentCabinet* agent, AddonCabinet* addon, int index) {
        var cacheId = agent->ItemCaches[index].Id;
        if (cacheId != 0)
            return Svc.Get<OwnershipService>().GetItemIdFromLookups(cacheId);

        if (agent->Items == null)
            return 0;

        var itemsIndex = addon->ItemSlots[index].ItemsArrayIndex;
        if (itemsIndex >= agent->ItemCount)
            return 0;

        return Svc.Get<OwnershipService>().GetItemIdFromLookups(agent->Items[itemsIndex].Id);
    }

    private static unsafe void ApplyFullListLayout(AddonCabinet* addon, int count) {
        var list = addon->ItemList;
        if (list is null || count <= 0)
            return;

        list->SetItemCount(0);
        list->SetItemCount((short)count);
        list->UpdateListItems();
        list->RecalculateVisibleItems(true);
        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    private static unsafe int InferReportedCategoryItemCount(AddonCabinet* addon, AgentCabinet* agent) {
        var contiguous = ScanContiguousPopulatedCategoryItemCount(agent);

        var listCount = 0;
        var list = addon->ItemList;
        if (list is not null) {
            listCount = list->GetItemCount();
            if (listCount <= 0 && list->ListLength > 0)
                listCount = list->ListLength;
        }

        if (contiguous == 0)
            return listCount;

        // List count can lag cache fill or still reflect a prior category / filtered pass.
        if (listCount <= 0 || listCount > contiguous || contiguous > listCount)
            return contiguous;

        return listCount;
    }

    private static unsafe int InferCategoryItemCount(AddonCabinet* addon, AgentCabinet* agent)
        => InferReportedCategoryItemCount(addon, agent);

    private static unsafe int ScanContiguousPopulatedCategoryItemCount(AgentCabinet* agent) {
        var count = 0;
        while (count < MaxCategoryItems && agent->ItemCaches[count].Id != 0)
            count++;
        return count;
    }

    private static unsafe int ScanPopulatedCategoryItemCount(AgentCabinet* agent) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (agent->ItemCaches[i].Id != 0)
                lastIndex = i;
        }

        return lastIndex >= 0 ? lastIndex + 1 : 0;
    }

    private static unsafe void ClearStaleCabinetTail(AgentCabinet* agent, AddonCabinet* addon) {
        var reported = InferReportedCategoryItemCount(addon, agent);
        ClearCabinetRowRange(agent, addon, reported, MaxCategoryItems);
    }

    private static unsafe void ClearCabinetRowRange(AgentCabinet* agent, AddonCabinet* addon, int from, int toExclusive) {
        for (var i = from; i < toExclusive && i < MaxCategoryItems; i++) {
            agent->ItemCaches[i].Clear();
            ref var slot = ref addon->ItemSlots[i];
            slot.Name.Clear();
            slot.Unk68 = 0;
            slot.InventorySlotIndex = 0;
            slot.InventoryContainerType = 0;
            slot.ItemsArrayIndex = 0;
            slot.ConditionNormalized = 0;
        }
    }

    private void ClearFilterState() {
        ReleaseCategorySnapshot();
        _categoryItemCount = 0;
        _categoryIndex = uint.MaxValue;
        _displayToSource = [];
        _pendingFullListCount = 0;
        _snapshotCapturePasses = 0;
        ClearFilterLogSignatures();
    }

    public async ValueTask DisposeAsync() {
        if (_disposed)
            return;
        _disposed = true;
        await Svc.Framework.RunOnFrameworkThread(() => {
            Svc.Get<OwnershipService>().ArmoireOwnershipChanged -= OnArmoireOwnershipChanged;
            _addonController.Dispose();
            ClearFilterState();
        });
    }
}
