using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Controllers;
using System.Threading.Tasks;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule;

namespace GlamourLog.Features.Cabinet;

internal sealed partial class CabinetListHandler : ListHandlerBase, IAsyncDisposable {
    private bool _disposed;
    private const string AddonName = "Cabinet";

    private readonly AddonController<AddonCabinet> _addonController;

    // last full-category capture; kept after Project so filter-off can un-project
    private CategoryRowSnapshot[] _rows = [];
    private uint[] _itemIds = [];
    private uint _categoryIndex = uint.MaxValue;
    private int _projectedVisible = -1;
    private bool _applyWhenReady;
    private bool _logNextApply; // emit apply debug once after a discrete transition

    public unsafe CabinetListHandler() : base(13, [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()]) {
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

    private bool ShouldExcludeItem(uint itemId) => itemId == 0 || Filters.Any(f => f.IsEnabled && f.ShouldHide(itemId));

    private bool HasCaptureFor(uint categoryIndex) => _rows.Length > 0 && _categoryIndex == categoryIndex;

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
        if (agent is null) {
            ClearFilterState();
            return;
        }

        if (!IsFilteringActive) {
            // undo our projection from the kept capture — native rebuild does not restore ItemSlots
            if (HasCaptureFor(addon->CategoryIndex)) {
                Restore(agent, addon);
                LogFilterDebug(nameof(ApplyConfigChange), $"filters disabled; restored {_rows.Length} rows");
                ReleaseRows();
            }
            else {
                LogFilterDebug(nameof(ApplyConfigChange), "filters disabled; no capture to restore");
            }

            _applyWhenReady = false;
            _logNextApply = false;
            return;
        }

        _logNextApply = true;
        LogFilterDebug(nameof(ApplyConfigChange), $"filters enabled for category {addon->CategoryIndex}");

        if (HasCaptureFor(addon->CategoryIndex)) {
            Project(agent, addon);
            return;
        }

        _applyWhenReady = true;
        TryApplyFilter(addon);
    }

    private unsafe void OnArmoireOwnershipChanged() {
        if (!IsFilteringActive)
            return;

        LogFilterDebug(nameof(OnArmoireOwnershipChanged), "ownership changed; scheduling list refresh");
        Svc.Framework.RunOnFrameworkThread(() => {
            ReleaseRows(); // deposited set changed — capture is stale
            _logNextApply = true;
            _applyWhenReady = true;
            if (Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName) is not null and var addon)
                addon->OnRefresh(0, null);
            else
                LogFilterDebug(nameof(OnArmoireOwnershipChanged), "cabinet addon not open");
        });
    }

    private unsafe void OnPreRefresh(AddonCabinet* addon) {
        if (!IsFilteringActive)
            return;

        CabinetGearsetLookup.Invalidate();
        // no PreRefresh restore — native owns the list across its own refreshes
    }

    private unsafe void OnPostRefresh(AddonCabinet* addon) {
        if (addon is null || !IsFilteringActive)
            return;

        TryApplyFilter(addon);
    }

    private unsafe void OnAddonUpdate(AddonCabinet* addon) {
        if (addon is null || !IsFilteringActive)
            return;

        if (_applyWhenReady)
            TryApplyFilter(addon);

        // native refresh with rows clears this; re-assert while filter leaves the list empty
        if (ShouldShowEmptyListMessage(addon)) {
            SetEmptyListMessageVisible((AtkUnitBase*)addon, true);
        }
    }

    private unsafe void OnFinalize(AddonCabinet* addon) => ClearFilterState();

    private unsafe void TryApplyFilter(AddonCabinet* addon) {
        var agent = AgentCabinet.Instance();
        if (agent is null)
            return;

        if (_categoryIndex != addon->CategoryIndex) {
            ReleaseRows();
            _categoryIndex = addon->CategoryIndex;
            _logNextApply = true;
            LogFilterDebug(nameof(TryApplyFilter), $"tracking category {_categoryIndex}");
        }

        if (agent->PendingUpdate || !IsCategoryReady(agent, addon)) {
            _applyWhenReady = true;
            return; // wait — do not re-enter OnRefresh from here (stack overflow CTD)
        }

        if (HasCaptureFor(addon->CategoryIndex)) {
            _applyWhenReady = false;
            Project(agent, addon); // silent re-apply unless a transition armed _logNextApply
            return;
        }

        if (!TryCapture(agent, addon)) {
            _applyWhenReady = true;
            return;
        }

        _applyWhenReady = false;
        _logNextApply = true;
        LogFilterDebug(nameof(TryCapture), $"captured {_rows.Length} rows for category {_categoryIndex}");
        Project(agent, addon);
        // keep _rows so filter-off can Restore
    }

    private unsafe bool TryCapture(AgentCabinet* agent, AddonCabinet* addon) {
        var capacity = Math.Min(agent->ItemCaches.Length, addon->ItemSlots.Length);
        var count = ReadCategoryItemCount(addon, agent);
        if (count is < 0 || count > capacity)
            return false;

        for (var i = 0; i < count; i++) {
            if (agent->ItemCaches[i].Id == 0)
                return false;
        }

        var ownership = Svc.Get<OwnershipService>();
        var rows = count == 0 ? [] : new CategoryRowSnapshot[count];
        var itemIds = count == 0 ? [] : new uint[count];
        for (var i = 0; i < count; i++) {
            rows[i] = new CategoryRowSnapshot {
                Slot = ItemSlotProjection.Capture(ref addon->ItemSlots[i], ref agent->ItemCaches[i]),
                Cache = ItemCacheProjection.Capture(ref agent->ItemCaches[i]),
            };
            itemIds[i] = rows[i].Cache.Id != 0
                ? ownership.GetItemIdFromLookups(rows[i].Cache.Id)
                : ResolveRowItemId(agent, addon, i);
        }

        ReleaseRows();
        _categoryIndex = addon->CategoryIndex;
        _rows = rows;
        _itemIds = itemIds;
        return true;
    }

    private unsafe int Project(AgentCabinet* agent, AddonCabinet* addon) {
        var visible = new List<int>(_rows.Length);
        for (var i = 0; i < _rows.Length; i++) {
            if (!ShouldExcludeItem(_itemIds[i]))
                visible.Add(i);
        }

        for (var display = 0; display < visible.Count; display++) {
            _rows[visible[display]].Slot.ApplyTo(ref addon->ItemSlots[display]);
            _rows[visible[display]].Cache.ApplyTo(ref agent->ItemCaches[display]);
        }

        ClearRowRange(agent, addon, visible.Count);
        ApplyListCount(addon, visible.Count);
        _projectedVisible = visible.Count;
        // native hides this when the list has rows; re-show after we filter them all out
        SetEmptyListMessageVisible((AtkUnitBase*)addon, visible.Count is 0);

        if (_logNextApply) {
            _logNextApply = false;
            LogFilterApplied(addon->CategoryIndex, _rows.Length, visible.Count, _itemIds, visible);
        }

        return visible.Count;
    }

    private unsafe void Restore(AgentCabinet* agent, AddonCabinet* addon) {
        for (var i = 0; i < _rows.Length; i++) {
            _rows[i].Slot.ApplyTo(ref addon->ItemSlots[i]);
            _rows[i].Cache.ApplyTo(ref agent->ItemCaches[i]);
        }

        ClearRowRange(agent, addon, _rows.Length);
        ApplyListCount(addon, _rows.Length);
        _projectedVisible = _rows.Length;
        SetEmptyListMessageVisible((AtkUnitBase*)addon, _rows.Length is 0);
    }

    private void ReleaseRows() {
        foreach (var row in _rows)
            row.Dispose();
        _rows = [];
        _itemIds = [];
        _projectedVisible = -1;
    }

    private unsafe bool ShouldShowEmptyListMessage(AddonCabinet* addon)
        => HasCaptureFor(addon->CategoryIndex) && _projectedVisible == 0;

    private static unsafe bool IsCategoryReady(AgentCabinet* agent, AddonCabinet* addon)
        => addon->CategoryIndex != uint.MaxValue
           && (agent->SelectedCategoryIndex == 0 || agent->SelectedCategoryIndex == addon->CategoryIndex + 1);

    private static unsafe int ReadCategoryItemCount(AddonCabinet* addon, AgentCabinet* agent) {
        var capacity = Math.Min(agent->ItemCaches.Length, addon->ItemSlots.Length);

        // NumberArray[0] is authoritative; slots past it are stale. Never write this array.
        var numberArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.CabinetStore);
        if (numberArray is not null && numberArray->IntArray is not null) {
            var count = numberArray->IntArray[0];
            if (count is >= 0 && count <= capacity)
                return count;
        }

        var contiguous = 0;
        while (contiguous < capacity && agent->ItemCaches[contiguous].Id != 0)
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

        var capacity = addon->ItemSlots.Length;
        list->SetItemCount(0);
        list->SetItemCount((short)Math.Clamp(count, 0, capacity));
        if (count > 0) {
            list->UpdateListItems();
            list->RecalculateVisibleItems(true);
        }

        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    private static unsafe void ClearRowRange(AgentCabinet* agent, AddonCabinet* addon, int from) {
        var capacity = Math.Min(agent->ItemCaches.Length, addon->ItemSlots.Length);
        for (var i = from; i < capacity; i++) {
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
        ReleaseRows();
        _categoryIndex = uint.MaxValue;
        _projectedVisible = -1;
        _applyWhenReady = false;
        _logNextApply = false;
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
