using clib.TaskSystem;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed class StoreAllDresserTask : TaskBase {
    private const string Crystallize = "MiragePrismPrismBoxCrystallize";
    private const string PrismBox = "MiragePrismPrismBox";
    private const string SetConvert = "MiragePrismPrismSetConvert";
    private const string SetConvertC = "MiragePrismPrismSetConvertC";
    private const int CategoryCount = 6;
    private const int MaxCategoryItems = 140;
    private const int StoreAsGlamourButtonId = 27;
    private const int CloseConvertButtonId = 26;
    private const uint OutfitAlreadyStoredNodeId = 12;

    private unsafe bool OutfitAlreadyStored => Svc.GameGui.TryGetAddon<AtkUnitBase>(SetConvert, out var addon) && addon->TryGetNodeById<AtkResNode>(OutfitAlreadyStoredNodeId, out _);

    protected override async Task Execute() {
        ErrorIf(!IsPrismBoxReady(), "Dresser not ready");
        for (var categoryIndex = 0; categoryIndex < CategoryCount; categoryIndex++) {
            await SelectCategory(categoryIndex);
            await StoreAllInCurrentCategory();
        }
    }

    private async Task SelectCategory(int categoryIndex) {
        using var scope = BeginScope(nameof(SelectCategory));
        ErrorIf(!TrySelectCategory(categoryIndex), "Failed to switch category");

        Log($"Switching to category {categoryIndex + 1}/{CategoryCount}");
        await WaitUntil(() => IsCategoryReady(categoryIndex), "WaitForCategory");
        await NextFrame(2);
    }

    private async Task StoreAllInCurrentCategory() {
        using var scope = BeginScope(nameof(StoreAllInCurrentCategory));
        while (true) {
            unsafe {
                if (GetData() is not null and var data)
                    Svc.Get<CrystallizeListHandler>().RestoreCategory(data, data->CrystallizeCategory);
            }

            if (!TryGetNextDresserItem(out var row))
                break;

            var itemId = ItemUtil.GetBaseId(row.ItemId).ItemId;
            Log($"Storing dresser item #{itemId}");
            await StoreItem(row, itemId);
        }
    }

    // flow is yesno (sometimes) -> convert -> confirm
    private async Task StoreItem(PrismBoxCrystallizeItem row, uint itemId) {
        using var scope = BeginScope(nameof(StoreItem));

        if (IsDresserStoreComplete(itemId)) {
            await ResyncCategoryAfterStore();
            return;
        }

        ErrorIf(!TryOpenConvert(row), $"Failed to open {nameof(SetConvert)}");

        // items part of an already started outfit have a yesno prompt before the convert addon
        await WaitUntilSkipping(() => IsConvertDoneOrReady(itemId), "WaitForConvert", UiSkipOptions.YesNo);

        if (IsDresserStoreComplete(itemId)) {
            await ResyncCategoryAfterStore();
            return;
        }

        if (!AtkUnitBase.IsAddonReady(SetConvert))
            ErrorIf(!IsDresserStoreComplete(itemId), "SetConvert closed unexpectedly");

        if (OutfitAlreadyStored) {
            Log("Outfit already stored in dresser");
            if (AtkUnitBase.IsAddonReady(SetConvert))
                TryClickAddonButton(SetConvert, CloseConvertButtonId);
            await WaitUntil(() => AtkUnitBase.IsAddonReady(Crystallize) || !AtkUnitBase.IsAddonReady(SetConvert), "WaitForCrystallizeReturn");
            return;
        }

        // callback can dismiss SetConvert before confirm opens or something so click the button
        if (!IsDresserStoreComplete(itemId))
            ErrorIf(!TryClickAddonButton(SetConvert, StoreAsGlamourButtonId) && !IsDresserStoreComplete(itemId), "Failed to click Store as Glamour");

        if (!IsDresserStoreComplete(itemId)) {
            await WaitUntilSkipping(() => AtkUnitBase.IsAddonReady(SetConvertC) || IsDresserStoreComplete(itemId), "WaitForStoreConfirm", UiSkipOptions.YesNo);
            if (!IsDresserStoreComplete(itemId))
                ErrorIf(!TryConfirmDresserStore(itemId), "Failed to confirm dresser store");
        }

        await WaitUntilSkipping(() => IsDresserStoreComplete(itemId), "WaitForStored", UiSkipOptions.YesNo);
        await ResyncCategoryAfterStore();
    }

    private static unsafe bool IsConvertDoneOrReady(uint itemId) {
        if (IsDresserStoreComplete(itemId))
            return true;
        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(SetConvert, out var addon))
            return false;
        var button = addon->GetComponentButtonById(StoreAsGlamourButtonId);
        return button is not null && button->IsEnabled;
    }

    private static bool IsDresserStoreComplete(uint itemId) {
        if (!Svc.Get<OwnershipService>().IsCrystallizeItemFullyDeposited(itemId) && !IsItemRemovedFromCategory(itemId))
            return false;

        // move on once convert addon is gone regardless of how it was closed
        return !AtkUnitBase.IsAddonReady(SetConvert) && !AtkUnitBase.IsAddonReady(SetConvertC);
    }

    private static unsafe bool IsPrismBoxReady()
        => AtkUnitBase.IsAddonReady(Crystallize) && AtkUnitBase.IsAddonReady(PrismBox) && MirageManager.Instance()->PrismBoxLoaded;

    private static unsafe bool TrySelectCategory(int categoryIndex) {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(Crystallize);
        var data = GetData();
        if (addon is null || data is null)
            return false;

        var nextBtn = addon->GetComponentButtonById(7);
        var dropDown = FindCategoryDropDown(addon);
        if (nextBtn is null || dropDown is null)
            return false;

        if (IsCategoryReady(categoryIndex))
            return true;

        Svc.Get<CrystallizeListHandler>().PrepareAutomationCategorySwitch(categoryIndex);

        if (dropDown->GetSelectedItemIndex() != categoryIndex)
            nextBtn->Click();

        data->CrystallizeCategory = categoryIndex;
        Svc.Log.Debug($"Requested category {categoryIndex}: dropdown={dropDown->GetSelectedItemIndex()}, agent={data->CrystallizeCategory}");
        return true;
    }

    private static unsafe bool IsCategoryReady(int categoryIndex) {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(Crystallize);
        var data = GetData();
        if (addon is null || data is null)
            return false;

        var dropDown = FindCategoryDropDown(addon);
        if (dropDown is null)
            return false;

        // make sure agent category, addon category, and ListHandler snapshot all match before doing anything
        return data->CrystallizeCategory == categoryIndex && dropDown->GetSelectedItemIndex() == categoryIndex && Svc.Get<CrystallizeListHandler>().IsCategoryLoaded(categoryIndex);
    }

    private static unsafe AtkComponentDropDownList* FindCategoryDropDown(AtkUnitBase* addon) {
        AtkComponentDropDownList* fallback = null;
        var data = GetData();
        var currentCategory = data is not null ? data->CrystallizeCategory : -1;

        foreach (var nodePtr in addon->UldManager.Nodes) {
            var node = nodePtr.Value;
            if (node is null)
                continue;

            var dropDown = node->GetAsAtkComponentDropdownList();
            if (dropDown is null)
                continue;

            if (currentCategory >= 0 && dropDown->GetSelectedItemIndex() == currentCategory)
                return dropDown;

            if (fallback is null)
                fallback = dropDown;
        }

        return fallback;
    }

    private static unsafe bool TryOpenConvert(PrismBoxCrystallizeItem row) {
        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(Crystallize, out var crystallizeAddon) || !Svc.GameGui.TryGetAddon<AtkUnitBase>(PrismBox, out var prismBoxAddon) || !TrySelectCrystallizeItem(row))
            return false;

        var handle = (ItemHandle)row.ItemId;
        if (row.Inventory == InventoryType.Invalid || !handle.TrySetItemLocation())
            return false;

        return AgentMiragePrismPrismSetConvert.Instance()->Open(row.ItemId, handle.ItemLocation.Container, handle.ItemLocation.Slot, crystallizeAddon->Id, prismBoxAddon->Id, enableStoring: true);
    }

    private static unsafe bool TrySelectCrystallizeItem(PrismBoxCrystallizeItem row) {
        var data = GetData();
        if (data is null || row.ItemId == 0)
            return false;

        var count = InferPopulatedCategoryItemCount(data);
        for (var i = 0; i < count; i++) {
            ref var candidate = ref data->CrystallizeItems[i];
            if (candidate.ItemId != row.ItemId)
                continue;

            if (row.Inventory != InventoryType.Invalid && (candidate.Inventory != row.Inventory || candidate.Slot != row.Slot))
                continue;

            data->CrystallizeItemIndex = (ushort)i;
            data->CrystallizeSelectedItem = candidate;
            return true;
        }

        return false;
    }

    // storing changes the native list (obv), so invalidate the ListHandler snapshot and wait for a recapture so TryGetNextDresserItem sees the updated rows
    private async Task ResyncCategoryAfterStore() {
        static unsafe int GetCategory() {
            var data = GetData();
            return data is null ? -1 : data->CrystallizeCategory;
        }

        var category = GetCategory();
        if (category < 0)
            return;

        Svc.Get<CrystallizeListHandler>().NotifyCategoryItemStored(category);
        await WaitUntil(() => IsCategoryReady(category), "WaitForListResync");
    }

    private static unsafe bool TryClickAddonButton(string addonName, uint buttonId) {
        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(addonName, out var addon))
            return false;

        var button = addon->GetComponentButtonById(buttonId);
        if (button is null || !button->IsEnabled)
            return false;

        button->Click();
        return true;
    }

    // gotta do a callback and not button click to bypass the checkbox cause cba clicking it
    private static unsafe bool TryConfirmDresserStore(uint itemId) {
        if (IsDresserStoreComplete(itemId))
            return true;

        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(SetConvertC, out var addon))
            return false;

        Span<AtkValue> values = stackalloc AtkValue[1];
        values[0].SetInt(0);

        fixed (AtkValue* ptr = values)
            return addon->FireCallback(1, ptr, close: true);
    }

    private static unsafe bool TryGetNextDresserItem(out PrismBoxCrystallizeItem item) {
        item = default;

        var data = GetData();
        if (data is null)
            return false;

        var ownership = Svc.Get<OwnershipService>();
        var count = InferPopulatedCategoryItemCount(data);

        for (var i = 0; i < count; i++) {
            var row = data->CrystallizeItems[i];
            if (row.ItemId == 0)
                break;

            var itemId = ItemUtil.GetBaseId(row.ItemId).ItemId;
            if (itemId == 0 || !MirageStoreSetItemLookup.TryGetRow(itemId, out _))
                continue;

            if (ownership.IsCrystallizeItemFullyDeposited(itemId) || ownership.IsArmoireEligible(itemId))
                continue;

            var handle = (ItemHandle)itemId;
            if (row.Inventory == InventoryType.Invalid || !handle.TrySetItemLocation())
                continue;

            item = row;
            return true;
        }

        return false;
    }

    private static unsafe bool IsItemRemovedFromCategory(uint itemId) {
        var data = GetData();
        if (data is null)
            return false;

        var targetId = ItemUtil.GetBaseId(itemId).ItemId;
        var count = InferPopulatedCategoryItemCount(data);
        for (var i = 0; i < count; i++) {
            var rowId = data->CrystallizeItems[i].ItemId;
            if (rowId == 0)
                break;

            if (ItemUtil.GetBaseId(rowId).ItemId == targetId)
                return false;
        }

        return true;
    }

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }

    // after RestoreCategory, the agent buffer holds the full tab
    // CrystallizeItemCount can lag behind or be zero while slots are still populated so scan all slots and use the highest populated index + 1.
    private static unsafe int InferPopulatedCategoryItemCount(MiragePrismPrismBoxData* data) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }

        if (lastIndex >= 0)
            return lastIndex + 1;

        return data->CrystallizeItemCount > 0 ? data->CrystallizeItemCount : 0;
    }
}
