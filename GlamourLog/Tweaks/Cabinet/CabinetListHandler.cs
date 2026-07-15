using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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

    private CategoryRowSnapshot[] _rows = [];
    private uint[] _itemIds = [];
    private uint _categoryIndex = uint.MaxValue;
    private int _pendingFullListCount;
    private bool _applyWhenReady;

    public unsafe CabinetListHandler() {
        _filters = [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()];

        _addonController = new AddonController<AddonCabinet> {
            AddonName = AddonName,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnUpdate = OnAddonUpdate,
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

        if (!IsFilteringActive) {
            _pendingFullListCount = _rows.Length;
            ClearFilterState(keepPendingFullList: true);
            addon->OnRefresh(0, null);
            return;
        }

        _applyWhenReady = true;
        addon->OnRefresh(0, null);
    }

    private unsafe void OnArmoireOwnershipChanged() {
        if (!IsFilteringActive)
            return;

        Svc.Framework.RunOnFrameworkThread(() => {
            ClearFilterLogSignatures();
            _applyWhenReady = true;
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

        // never restore across a tab switch or while native is rebuilding
        if (_categoryIndex != addon->CategoryIndex || agent->PendingUpdate || !IsCategoryReady(agent, addon)) {
            if (_categoryIndex != addon->CategoryIndex)
                DropSnapshot(addon->CategoryIndex);
            return;
        }

        if (_rows.Length > 0)
            Restore(agent, addon);
    }

    private unsafe void OnPostRefresh(AddonCabinet* addon) {
        if (addon is null)
            return;

        if (!IsFilteringActive) {
            if (_pendingFullListCount > 0) {
                ApplyListCount(addon, _pendingFullListCount);
                _pendingFullListCount = 0;
            }

            if (AgentCabinet.Instance() is not null and var filterOffAgent)
                LogFilterOffState(nameof(OnPostRefresh), addon, filterOffAgent);
            return;
        }

        TryApplyFilter(addon, fromRefresh: true);
    }

    private unsafe void OnAddonUpdate(AddonCabinet* addon) {
        if (addon is null || !IsFilteringActive || !_applyWhenReady)
            return;

        TryApplyFilter(addon, fromRefresh: false);
    }

    private unsafe void OnFinalize(AddonCabinet* addon) => ClearFilterState();

    private unsafe void TryApplyFilter(AddonCabinet* addon, bool fromRefresh) {
        var agent = AgentCabinet.Instance();
        if (agent is null)
            return;

        if (_categoryIndex != addon->CategoryIndex)
            DropSnapshot(addon->CategoryIndex);

        if (agent->PendingUpdate || !IsCategoryReady(agent, addon)) {
            _applyWhenReady = true;
            if (fromRefresh)
                LogSnapshotUnavailableOnce(addon, agent);
            return; // wait — do not re-enter OnRefresh from here (stack overflow CTD)
        }

        if (!TryCapture(agent, addon)) {
            _applyWhenReady = true;
            if (fromRefresh)
                LogSnapshotUnavailableOnce(addon, agent);
            return;
        }

        _applyWhenReady = false;
        var visible = Project(agent, addon);
        LogFilterOnState(nameof(OnPostRefresh), addon, agent, _rows.Length, visible);
    }

    private void DropSnapshot(uint categoryIndex) {
        ReleaseRows();
        _categoryIndex = categoryIndex;
        ClearFilterLogSignatures();
    }

    private unsafe bool TryCapture(AgentCabinet* agent, AddonCabinet* addon) {
        var count = ReadCategoryItemCount(addon, agent);
        if (count < 0 || count > MaxCategoryItems)
            return false;

        for (var i = 0; i < count; i++) {
            if (agent->ItemCaches[i].Id == 0)
                return false;
        }

        // projected list is shorter than the last full snapshot — wait for restore+native
        if (_rows.Length > 0 && count < _rows.Length && _categoryIndex == addon->CategoryIndex)
            return false;

        ReleaseRows();
        _categoryIndex = addon->CategoryIndex;

        if (count == 0) {
            _rows = [];
            _itemIds = [];
            return true;
        }

        _rows = new CategoryRowSnapshot[count];
        _itemIds = new uint[count];
        for (var i = 0; i < count; i++) {
            _rows[i] = new CategoryRowSnapshot {
                Slot = ItemSlotProjection.Capture(ref addon->ItemSlots[i], ref agent->ItemCaches[i]),
                Cache = ItemCacheProjection.Capture(ref agent->ItemCaches[i]),
            };
            _itemIds[i] = _rows[i].Cache.Id != 0
                ? Svc.Get<OwnershipService>().GetItemIdFromLookups(_rows[i].Cache.Id)
                : ResolveRowItemId(agent, addon, i);
        }

        return true;
    }

    private unsafe int Project(AgentCabinet* agent, AddonCabinet* addon) {
        var visible = new List<int>(_rows.Length);
        for (var i = 0; i < _rows.Length; i++) {
            if (!ShouldExcludeItem(_itemIds[i]))
                visible.Add(i);
        }

        LogFilterRebuildSummary(addon->CategoryIndex, _rows.Length, visible.Count, _itemIds, visible);

        for (var display = 0; display < visible.Count; display++) {
            _rows[visible[display]].Slot.ApplyTo(ref addon->ItemSlots[display]);
            _rows[visible[display]].Cache.ApplyTo(ref agent->ItemCaches[display]);
        }

        ClearRowRange(agent, addon, visible.Count, MaxCategoryItems);
        ApplyListCount(addon, visible.Count);
        LogApplyPipelineResult(addon, visible.Count, _rows.Length);
        return visible.Count;
    }

    private unsafe void Restore(AgentCabinet* agent, AddonCabinet* addon) {
        for (var i = 0; i < _rows.Length; i++) {
            _rows[i].Slot.ApplyTo(ref addon->ItemSlots[i]);
            _rows[i].Cache.ApplyTo(ref agent->ItemCaches[i]);
        }

        ClearRowRange(agent, addon, _rows.Length, MaxCategoryItems);
        ApplyListCount(addon, _rows.Length);
    }

    private void ReleaseRows() {
        foreach (var row in _rows)
            row.Dispose();
        _rows = [];
        _itemIds = [];
    }

    private static unsafe bool IsCategoryReady(AgentCabinet* agent, AddonCabinet* addon)
        => addon->CategoryIndex != uint.MaxValue
           && (agent->SelectedCategoryIndex == 0 || agent->SelectedCategoryIndex == addon->CategoryIndex + 1);

    private static unsafe int ReadCategoryItemCount(AddonCabinet* addon, AgentCabinet* agent) {
        // NumberArray[0] is authoritative; slots past it are stale. Never write this array.
        var numberArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.CabinetStore);
        if (numberArray is not null && numberArray->IntArray is not null) {
            var count = numberArray->IntArray[0];
            if (count is >= 0 and <= MaxCategoryItems)
                return count;
        }

        var contiguous = 0;
        while (contiguous < MaxCategoryItems && agent->ItemCaches[contiguous].Id != 0)
            contiguous++;

        var list = addon->ItemList;
        var listCount = list is not null ? list->GetItemCount() : 0;
        if (listCount <= 0 && list is not null && list->ListLength > 0)
            listCount = list->ListLength;

        return contiguous > 0 ? contiguous : listCount;
    }

    private static unsafe uint ResolveRowItemId(AgentCabinet* agent, AddonCabinet* addon, int index) {
        var cacheId = agent->ItemCaches[index].Id;
        if (cacheId != 0)
            return Svc.Get<OwnershipService>().GetItemIdFromLookups(cacheId);

        if (agent->Items == null)
            return 0;

        var itemsIndex = addon->ItemSlots[index].ItemsArrayIndex;
        return itemsIndex < agent->ItemCount
            ? Svc.Get<OwnershipService>().GetItemIdFromLookups(agent->Items[itemsIndex].Id)
            : 0;
    }

    private static unsafe void ApplyListCount(AddonCabinet* addon, int count) {
        var list = addon->ItemList;
        if (list is null)
            return;

        list->SetItemCount(0);
        list->SetItemCount((short)Math.Clamp(count, 0, MaxCategoryItems));
        if (count > 0) {
            list->UpdateListItems();
            list->RecalculateVisibleItems(true);
        }

        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    private static unsafe void ClearRowRange(AgentCabinet* agent, AddonCabinet* addon, int from, int toExclusive) {
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

    private void ClearFilterState(bool keepPendingFullList = false) {
        ReleaseRows();
        _categoryIndex = uint.MaxValue;
        if (!keepPendingFullList)
            _pendingFullListCount = 0;
        _applyWhenReady = false;
        ClearFilterLogSignatures();
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

        internal static ItemSlotProjection Capture(ref AddonCabinet.ItemSlot source, ref ItemCache cache) {
            fixed (Utf8String* cacheName = &cache.Name)
            fixed (Utf8String* slotName = &source.Name) {
                var nameSource = cacheName->Length > 0 ? cacheName : slotName;
                return new ItemSlotProjection {
                    IconId = source.Unk68 != 0 ? source.Unk68 : cache.IconId,
                    InventorySlotIndex = source.InventorySlotIndex,
                    InventoryContainerType = source.InventoryContainerType,
                    ItemsArrayIndex = source.ItemsArrayIndex,
                    ConditionNormalized = source.ConditionNormalized,
                    _nameClone = Utf8String.FromUtf8String(nameSource),
                };
            }
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
            _nameClone->Dtor(true);
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
            _nameClone->Dtor(true);
            _nameClone = null;
        }
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
