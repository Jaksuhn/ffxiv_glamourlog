using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed class StoreAllDresserTask : AutoTask {
    private const string Crystallize = "MiragePrismPrismBoxCrystallize";
    private const string PrismBox = "MiragePrismPrismBox";
    private const string SetConvert = "MiragePrismPrismSetConvert";
    private const string SetConvertC = "MiragePrismPrismSetConvertC";
    private const int CategoryCount = 6;
    private const int MaxCategoryItems = 140;
    private const int StoreAsGlamourButtonId = 27;
    private const int CloseConvertButtonId = 26;
    private const uint OutfitAlreadyStoredNodeId = 12;

    protected override async Task Execute() {
        ErrorIf(!IsDresserUiReady(), "Dresser not ready");
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
            RestoreFullCategoryForCurrentTab();
            if (!TryGetNextDresserItem(out var row))
                break;

            var itemId = ItemUtil.GetBaseId(row.ItemId).ItemId;
            Log($"Storing dresser item {itemId}");
            await StoreItem(row, itemId);
        }
    }

    private async Task StoreItem(PrismBoxCrystallizeItem row, uint itemId) {
        using var scope = BeginScope(nameof(StoreItem));
        ErrorIf(!TryOpenConvert(row), "Failed to open dresser convert flow");

        await WaitUnlessYesno(() => IsItemStored(itemId)
            || AtkUnitBase.IsAddonReady(SetConvert) && IsSetConvertReady(), "WaitForConvert");

        if (IsItemStored(itemId))
            return;

        ErrorIf(!AtkUnitBase.IsAddonReady(SetConvert), "Dresser convert window closed unexpectedly");

        if (IsOutfitAlreadyStored()) {
            Log("Outfit already stored in dresser");
            ErrorIf(!TryClickAddonButton(SetConvert, CloseConvertButtonId), "Failed to close dresser convert window");
            await WaitUntil(() => AtkUnitBase.IsAddonReady(Crystallize), "WaitForCrystallizeReturn");
            return;
        }

        ErrorIf(!TryClickAddonButton(SetConvert, StoreAsGlamourButtonId), "Failed to click dresser store button");

        await WaitUnlessYesno(() => AtkUnitBase.IsAddonReady(SetConvertC), "WaitForStoreConfirm");
        ErrorIf(!TryConfirmDresserStore(), "Failed to confirm dresser store");

        await WaitUnlessYesno(() => IsDresserStoreComplete(itemId), "WaitForStored");
        await ResyncCategoryAfterStore();
    }

    private async Task WaitUnlessYesno(Func<bool> done, string scopeName) {
        await WaitUntil(() => {
            if (TryAcceptSelectYesno())
                return false;
            return done();
        }, scopeName);
    }

    private static bool IsDresserStoreComplete(uint itemId)
        => IsStoreFlowIdle() && (IsItemStored(itemId) || IsItemRemovedFromCategory(itemId));

    private static bool IsStoreFlowIdle()
        => AtkUnitBase.IsAddonReady(Crystallize)
            && !AtkUnitBase.IsAddonReady(SetConvert)
            && !AtkUnitBase.IsAddonReady(SetConvertC);

    private static bool TryAcceptSelectYesno() {
        if (!AtkUnitBase.IsAddonReady("SelectYesno"))
            return false;

        AddonSelectYesno.Yes();
        return true;
    }

    private static unsafe bool IsDresserUiReady()
        => AtkUnitBase.IsAddonReady(Crystallize) && AtkUnitBase.IsAddonReady(PrismBox) && MirageManager.Instance()->PrismBoxLoaded;

    private static unsafe void RestoreFullCategoryForCurrentTab() {
        var data = GetData();
        if (data is null)
            return;

        Svc.Get<CrystallizeListHandler>().RestoreFullCategoryForAutomation(data, data->CrystallizeCategory);
    }

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

        return data->CrystallizeCategory == categoryIndex
            && dropDown->GetSelectedItemIndex() == categoryIndex
            && Svc.Get<CrystallizeListHandler>().IsCategoryLoaded(categoryIndex);
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
        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(Crystallize, out var crystallizeAddon)
            || !Svc.GameGui.TryGetAddon<AtkUnitBase>(PrismBox, out var prismBoxAddon)
            || !TrySelectCrystallizeItem(row))
            return false;

        var inventory = row.Inventory;
        var slot = row.Slot;
        if (inventory == InventoryType.Invalid && !TryFindInventorySlot(row.ItemId, out inventory, out slot))
            return false;

        return AgentMiragePrismPrismSetConvert.Instance()->Open(
            row.ItemId, inventory, slot, crystallizeAddon->Id, prismBoxAddon->Id, enableStoring: true);
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

            if (row.Inventory != InventoryType.Invalid
                && (candidate.Inventory != row.Inventory || candidate.Slot != row.Slot))
                continue;

            data->CrystallizeItemIndex = (ushort)i;
            data->CrystallizeSelectedItem = candidate;
            return true;
        }

        return false;
    }

    private static bool IsItemStored(uint itemId)
        => Svc.Get<OwnershipService>().IsCrystallizeItemFullyDeposited(itemId);

    private static unsafe bool IsOutfitAlreadyStored() {
        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(SetConvert, out var addon))
            return false;

        var node = addon->GetNodeById<AtkResNode>(OutfitAlreadyStoredNodeId);
        return node is not null && node->IsVisible();
    }

    private async Task ResyncCategoryAfterStore() {
        unsafe int GetCategory() {
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

    // Callback bypasses the "don't show again" checkbox on MiragePrismPrismSetConvertC.
    private static unsafe bool TryConfirmDresserStore() {
        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(SetConvertC, out var addon))
            return false;

        Span<AtkValue> values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;

        fixed (AtkValue* ptr = values)
            return addon->FireCallback(1, ptr, close: true);
    }

    private static unsafe bool IsSetConvertReady() {
        if (!Svc.GameGui.TryGetAddon<AtkUnitBase>(SetConvert, out var addon))
            return false;

        var button = addon->GetComponentButtonById(StoreAsGlamourButtonId);
        return button is not null && button->IsEnabled;
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

            if (row.Inventory == InventoryType.Invalid && !TryFindInventorySlot(row.ItemId, out _, out _))
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

    private static unsafe bool TryFindInventorySlot(uint itemId, out InventoryType container, out int slot) {
        container = InventoryType.Invalid;
        slot = 0;

        var baseItemId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseItemId == 0)
            return false;

        ReadOnlySpan<InventoryType> containers = [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
            InventoryType.EquippedItems,
            InventoryType.ArmoryMainHand,
            InventoryType.ArmoryHead,
            InventoryType.ArmoryBody,
            InventoryType.ArmoryHands,
            InventoryType.ArmoryWaist,
            InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets,
            InventoryType.ArmoryOffHand,
            InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist,
            InventoryType.ArmoryRings,
            InventoryType.ArmorySoulCrystal,
        ];

        foreach (var inventoryType in containers) {
            var inventoryContainer = InventoryManager.Instance()->GetInventoryContainer(inventoryType);
            if (inventoryContainer is null || !inventoryContainer->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < inventoryContainer->GetSize(); slotIndex++) {
                var inventorySlot = inventoryContainer->GetInventorySlot(slotIndex);
                if (inventorySlot is null || inventorySlot->IsEmpty())
                    continue;

                if (ItemUtil.GetBaseId(inventorySlot->GetItemId()).ItemId != baseItemId)
                    continue;

                container = inventorySlot->GetInventoryType();
                slot = slotIndex;
                return true;
            }
        }

        return false;
    }

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }

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
