using System.Runtime.CompilerServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Controllers;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule;

namespace GlamourLog.Features.Cabinet;

internal sealed unsafe class CabinetListHandler : IDisposable {
    private const string AddonName = "Cabinet";
    private const int MaxCategoryItems = 140;
    private static readonly int ItemCacheSize = sizeof(ItemCache);
    private static readonly int ItemSlotSize = sizeof(AddonCabinet.ItemSlot);

    private readonly ICabinetRowFilter[] _filters;
    private readonly AddonController<AddonCabinet> _addonController;

    private byte[]? _itemCacheBackup;
    private byte[]? _itemSlotBackup;
    private int[] _displayToSource = [];
    private int _backupCount;
    private bool _hasBackup;
    private uint _backupCategoryIndex = uint.MaxValue;

    private Hook<AtkComponentListItemPopulator.PopulateWithRendererDelegate>? _populateHook;
    private AtkComponentList* _itemList;

    public CabinetListHandler() {
        _filters = [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()];

        _addonController = new AddonController<AddonCabinet> {
            AddonName = AddonName,
            OnSetup = OnSetup,
            OnPreRefresh = OnPreRefresh,
            OnRefresh = OnPostRefresh,
            OnFinalize = OnFinalize,
        };

        _addonController.Enable();
        Svc.Get<OwnershipService>().ArmoireOwnershipChanged += OnArmoireOwnershipChanged;
    }

    public void Dispose() {
        Svc.Get<OwnershipService>().ArmoireOwnershipChanged -= OnArmoireOwnershipChanged;
        DisablePopulateHook();
        _addonController.Dispose();
        ClearFilterState();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool ShouldExcludeItem(uint itemId)
        => itemId != 0 && _filters.Any(f => f.IsEnabled && f.ShouldHide(itemId));

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(() => {
            CabinetGearsetLookup.Invalidate();

            var addon = Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName);
            if (addon is null) {
                ClearFilterState();
                return;
            }

            if (!IsFilteringActive) {
                var fullCount = _backupCount;
                if (_hasBackup) {
                    RestoreDisplayItems(addon);
                    ClearFilterState();
                }

                if (fullCount > 0)
                    ApplyFullListLayout(addon, fullCount);
                else
                    addon->OnRefresh(0, null);

                return;
            }

            ClearFilterState();
            RebuildList(addon);
        });
    }

    private void OnArmoireOwnershipChanged() {
        if (!IsFilteringActive)
            return;

        Svc.Framework.RunOnFrameworkThread(() => {
            ClearFilterState();
            if (Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName) is not null and var addon)
                RebuildList(addon);
        });
    }

    private void OnSetup(AddonCabinet* addon) {
        _itemList = addon->ItemList;
        if (_itemList is null)
            return;

        var renderer = _itemList->FirstAtkComponentListItemRenderer;
        if (renderer is null)
            return;

        var populate = renderer->Populator.PopulateWithRenderer;
        if (populate is null)
            return;

        _populateHook ??= Svc.Hook.HookFromAddress<AtkComponentListItemPopulator.PopulateWithRendererDelegate>(populate, OnPopulateWithRendererDetour);
        _populateHook.Enable();
    }

    private void OnPreRefresh(AddonCabinet* addon) {
        CabinetGearsetLookup.Invalidate();

        if (addon is null)
            return;

        if (!IsFilteringActive) {
            if (_hasBackup)
                RestoreDisplayItems(addon);
            return;
        }

        PrepareFilterState(addon);
    }

    private void OnPostRefresh(AddonCabinet* addon) {
        if (addon is null)
            return;

        if (IsFilteringActive)
            ApplyFilteredListLayout(addon);
        else if (_backupCount > 0)
            ApplyFullListLayout(addon, _backupCount);
    }

    private void OnFinalize(AddonCabinet* addon) {
        if (addon is not null && _hasBackup)
            RestoreDisplayItems(addon);

        DisablePopulateHook();
        ClearFilterState();
        _itemList = null;
    }

    private void RebuildList(AddonCabinet* addon) {
        if (!IsFilteringActive) {
            if (_hasBackup)
                RestoreDisplayItems(addon);

            var agent = AgentCabinet.Instance();
            var count = agent is not null ? InferDisplayedItemCount(addon, agent) : _backupCount;
            if (count > 0)
                ApplyFullListLayout(addon, count);
            else
                addon->OnRefresh(0, null);

            return;
        }

        PrepareFilterState(addon);
        ApplyFilteredListLayout(addon);
    }

    private void PrepareFilterState(AddonCabinet* addon) {
        var agent = AgentCabinet.Instance();
        if (agent is null)
            return;

        var count = InferDisplayedItemCount(addon, agent);
        if (count <= 0) {
            _displayToSource = [];
            return;
        }

        if (!_hasBackup || _backupCategoryIndex != addon->PreviousCategoryIndex)
            SnapshotDisplayItems(agent, addon, count);
        else
            RestoreDisplayItems(addon);

        BuildDisplayToSourceMap(agent, count);
    }

    private void BuildDisplayToSourceMap(AgentCabinet* agent, int count) {
        var visible = new List<int>(count);
        for (var i = 0; i < count; i++) {
            if (!ShouldExcludeItem(agent->ItemCaches[i].Id))
                visible.Add(i);
        }

        _displayToSource = [.. visible];
    }

    private void ApplyFilteredListLayout(AddonCabinet* addon) {
        var list = addon->ItemList;
        if (list is null)
            return;

        list->SetItemCount((short)_displayToSource.Length);
        if (_displayToSource.Length > 0)
            list->UpdateListItems();

        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    private static void ApplyFullListLayout(AddonCabinet* addon, int count) {
        var list = addon->ItemList;
        if (list is null || count <= 0)
            return;

        list->SetItemCount((short)count);
        list->UpdateListItems();
        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    private void DisablePopulateHook() {
        _populateHook?.Disable();
        _populateHook?.Dispose();
        _populateHook = null;
    }

    private void OnPopulateWithRendererDetour(AtkEventListener* eventListener, int listItemIndex, AtkResNode** nodeList, AtkComponentListItemRenderer* renderer) {
        var sourceIndex = listItemIndex;
        if (IsFilteringActive && _displayToSource.Length > 0) {
            if ((uint)listItemIndex >= (uint)_displayToSource.Length)
                return;

            sourceIndex = _displayToSource[listItemIndex];
        }

        _populateHook!.Original(eventListener, sourceIndex, nodeList, renderer);
        renderer->ListItemIndex = sourceIndex;
    }

    private static int InferDisplayedItemCount(AddonCabinet* addon, AgentCabinet* agent) {
        var list = addon->ItemList;
        if (list is not null && list->ListLength > 0)
            return list->ListLength;

        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (agent->ItemCaches[i].Id != 0)
                lastIndex = i;
        }

        return lastIndex + 1;
    }

    private void SnapshotDisplayItems(AgentCabinet* agent, AddonCabinet* addon, int count) {
        _backupCount = count;
        _itemCacheBackup ??= new byte[MaxCategoryItems * ItemCacheSize];
        _itemSlotBackup ??= new byte[MaxCategoryItems * ItemSlotSize];

        for (var i = 0; i < count; i++) {
            CopyItemCacheToBackup(agent->ItemCaches[i], i);
            CopyItemSlotToBackup(addon->ItemSlots[i], i);
        }

        _hasBackup = true;
        _backupCategoryIndex = addon->PreviousCategoryIndex;
    }

    private void RestoreDisplayItems(AddonCabinet* addon) {
        if (!_hasBackup)
            return;

        var agent = AgentCabinet.Instance();
        if (agent is null)
            return;

        for (var i = 0; i < _backupCount; i++) {
            CopyItemCacheFromBackup(ref agent->ItemCaches[i], i);
            CopyItemSlotFromBackup(ref addon->ItemSlots[i], i);
        }
    }

    private void ClearFilterState() {
        _hasBackup = false;
        _backupCount = 0;
        _backupCategoryIndex = uint.MaxValue;
        _displayToSource = [];
    }

    private void CopyItemCacheToBackup(in ItemCache cache, int index) {
        var backup = _itemCacheBackup!;
        fixed (byte* dst = &backup[index * ItemCacheSize])
        fixed (ItemCache* src = &Unsafe.AsRef(in cache))
            Buffer.MemoryCopy(src, dst, ItemCacheSize, ItemCacheSize);
    }

    private void CopyItemCacheFromBackup(ref ItemCache cache, int index) {
        var backup = _itemCacheBackup!;
        fixed (byte* src = &backup[index * ItemCacheSize])
        fixed (ItemCache* dst = &cache)
            Buffer.MemoryCopy(src, dst, ItemCacheSize, ItemCacheSize);
    }

    private void CopyItemSlotToBackup(in AddonCabinet.ItemSlot slot, int index) {
        var backup = _itemSlotBackup!;
        fixed (byte* dst = &backup[index * ItemSlotSize])
        fixed (AddonCabinet.ItemSlot* src = &Unsafe.AsRef(in slot))
            Buffer.MemoryCopy(src, dst, ItemSlotSize, ItemSlotSize);
    }

    private void CopyItemSlotFromBackup(ref AddonCabinet.ItemSlot slot, int index) {
        var backup = _itemSlotBackup!;
        fixed (byte* src = &backup[index * ItemSlotSize])
        fixed (AddonCabinet.ItemSlot* dst = &slot)
            Buffer.MemoryCopy(src, dst, ItemSlotSize, ItemSlotSize);
    }
}
