using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Controllers;
using System.Threading.Tasks;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule;

namespace GlamourLog.Tweaks.Cabinet;

internal sealed partial class CabinetListHandler : ListHandlerBase, IPluginService, IAsyncDisposable {
    public int InitOrder => 10; // after ownership

    private bool _disposed;
    private const string AddonName = "Cabinet";

    private readonly AddonController<AddonCabinet> _addonController;

    private CategoryRowSnapshot[] _rows = []; // keep after Project so turning filters off can un-project ItemSlots
    private uint[] _itemIds = [];
    private uint _categoryIndex = uint.MaxValue;
    private int _projectedVisible = -1;
    private bool _applyWhenReady;
    private bool _logNextApply; // emit apply debug once after a discrete transition
    private bool _deferCapture; // skip reapplying until after a refresh finishes

    public unsafe CabinetListHandler() : base(13, [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()]) {
        _addonController = new AddonController<AddonCabinet> {
            AddonName = AddonName,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnUpdate = OnAddonUpdate,
            OnFinalize = OnFinalize,
        };

        IFramework.Get().Run(() => _addonController.Enable());
        OwnershipService.Get().ArmoireOwnershipChanged += OnArmoireOwnershipChanged;
    }

    private bool ShouldExcludeItem(uint itemId) => itemId == 0 || Filters.Any(f => f.IsEnabled && f.ShouldHide(itemId));
    private bool HasCaptureFor(uint categoryIndex) => _categoryIndex == categoryIndex && _projectedVisible >= 0;

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
            // native PendingUpdate rebuilds ItemCaches but not ItemSlots, unproject
            if (HasCaptureFor(addon->CategoryIndex)) {
                var restored = Restore(agent, addon);
                LogFilterDebug(nameof(ApplyConfigChange), $"filters disabled; restored {restored} rows");
                ReleaseRows();
            }
            else {
                LogFilterDebug(nameof(ApplyConfigChange), "filters disabled; no capture to restore");
            }

            _applyWhenReady = false;
            _logNextApply = false;
            _deferCapture = false;
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
        // run after update so list isn't reprojected w/ a pre-deposit capture
        Svc.Framework.RunOnTick(() => {
            var addon = Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName);
            if (addon is null) {
                ReleaseRows();
                LogFilterDebug(nameof(OnArmoireOwnershipChanged), "cabinet addon not open");
                return;
            }

            var agent = AgentCabinet.Instance();
            ReleaseRows();
            _deferCapture = true;
            ClearCabinetSelection(addon, agent);

            if (!IsFilteringActive) {
                LogFilterDebug(nameof(OnArmoireOwnershipChanged), "ownership changed; cleared selection (filters inactive)");
                return;
            }

            LogFilterDebug(nameof(OnArmoireOwnershipChanged), "ownership changed; scheduling filtered refresh");
            _logNextApply = true;
            _applyWhenReady = true;
            addon->OnRefresh(0, null);
        }, delayTicks: 1);
    }

    private unsafe void OnPreRefresh(AddonCabinet* addon) {
        if (!IsFilteringActive)
            return;

        CabinetGearsetLookup.Invalidate();
    }

    private unsafe void OnPostRefresh(AddonCabinet* addon) {
        if (addon is null || !IsFilteringActive)
            return;

        TryApplyFilter(addon);
    }

    private unsafe void OnAddonUpdate(AddonCabinet* addon) {
        if (addon is null)
            return;

        if (!IsFilteringActive) {
            // Restore() hides the empty message when unfiltering a non-empty list; native empty
            // categories often never put it back after a tab change, so sync from the live count.
            SyncIdleEmptyListMessage(addon);
            return;
        }

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
            _deferCapture = false;
            LogFilterDebug(nameof(TryApplyFilter), $"tracking category {_categoryIndex}");
        }

        if (agent->PendingUpdate || !IsCategoryReady(agent, addon)) {
            _applyWhenReady = true;
            return; // this'll cause a CTD if you mess with it while pending
        }

        if (HasCaptureFor(addon->CategoryIndex)) {
            var nativeCount = ReadCategoryItemCount(addon, agent);
            // stale InventorySlotIndex gets applied if you reproject the pre-deposit capture after a store was just done which results in a list that looks right but the clicks are blocked
            if (!_deferCapture && nativeCount >= 0 && nativeCount < _rows.Length) {
                LogFilterDebug(nameof(TryApplyFilter), $"native shrunk {_rows.Length}->{nativeCount}; dropping capture");
                ReleaseRows();
                _deferCapture = true;
                ClearCabinetSelection(addon, agent);
            }
            else if (!_deferCapture) {
                _applyWhenReady = false;
                Project(agent, addon); // silent re-apply unless a transition armed _logNextApply
                return;
            }
            else {
                ReleaseRows();
            }
        }

        if (!TryCapture(agent, addon)) {
            _applyWhenReady = true;
            return;
        }

        _deferCapture = false;
        _applyWhenReady = false;
        _logNextApply = true;
        LogFilterDebug(nameof(TryCapture), $"captured {_rows.Length} rows for category {_categoryIndex}");
        Project(agent, addon);
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

        var ownership = OwnershipService.Get();
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
        ClearCabinetSelection(addon, agent);
        _projectedVisible = visible.Count;
        // native hides this when the list has rows; re-show after we filter them all out
        SetEmptyListMessageVisible((AtkUnitBase*)addon, visible.Count is 0);

        if (_logNextApply) {
            _logNextApply = false;
            LogFilterApplied(addon->CategoryIndex, _rows.Length, visible.Count, _itemIds, visible);
        }

        return visible.Count;
    }

    // required because native category reload does not rewrite ItemSlots
    private unsafe int Restore(AgentCabinet* agent, AddonCabinet* addon) {
        for (var i = 0; i < _rows.Length; i++) {
            _rows[i].Slot.ApplyTo(ref addon->ItemSlots[i]);
            _rows[i].Cache.ApplyTo(ref agent->ItemCaches[i]);
        }

        ClearRowRange(agent, addon, _rows.Length);
        ApplyListCount(addon, _rows.Length);
        _projectedVisible = _rows.Length;
        SetEmptyListMessageVisible((AtkUnitBase*)addon, _rows.Length is 0);
        return _rows.Length;
    }

    private void ReleaseRows() {
        foreach (var row in _rows)
            row.Dispose();
        _rows = [];
        _itemIds = [];
        _projectedVisible = -1;
    }

    private static unsafe void ClearCabinetSelection(AddonCabinet* addon, AgentCabinet* agent) {
        if (addon->ItemList is not null)
            addon->ItemList->DeselectItem();
        if (agent is not null) {
            agent->SelectedIndex = 0;
            agent->SelectedItemId = 0;
        }
    }

    private unsafe bool ShouldShowEmptyListMessage(AddonCabinet* addon)
        => HasCaptureFor(addon->CategoryIndex) && _projectedVisible == 0;

    private unsafe void SyncIdleEmptyListMessage(AddonCabinet* addon) {
        var agent = AgentCabinet.Instance();
        if (agent is null || agent->PendingUpdate || !IsCategoryReady(agent, addon))
            return;

        var count = ReadCategoryItemCount(addon, agent);
        if (count >= 0)
            SetEmptyListMessageVisible((AtkUnitBase*)addon, count == 0);
    }

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
            return OwnershipService.Get().GetItemIdFromLookups(cacheId);

        if (agent->Items == null)
            return 0;

        var itemsIndex = addon->ItemSlots[index].ItemsArrayIndex;
        return itemsIndex < agent->ItemCount
            ? OwnershipService.Get().GetItemIdFromLookups(agent->Items[itemsIndex].Id)
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
        _deferCapture = false;
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
            OwnershipService.Get().ArmoireOwnershipChanged -= OnArmoireOwnershipChanged;
            _addonController.Dispose();
            ClearFilterState();
        });
    }
}
