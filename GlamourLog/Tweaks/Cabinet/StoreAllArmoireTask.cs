using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Threading.Tasks;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace GlamourLog.Features.Cabinet;

internal sealed class StoreAllArmoireTask : AutoTask {
    private const string AddonName = "Cabinet";
    private const int CategoryCount = 6; // TODO: don't hard code this
    private const uint UninitializedCategoryIndex = uint.MaxValue; // value before first render

    protected override async Task Execute() {
        ErrorIf(!AtkUnitBase.IsAddonReady(AddonName) || !IsCabinetLoaded(), "Cabinet not ready");
        for (var categoryIndex = 0; categoryIndex < CategoryCount; categoryIndex++) {
            await SelectCategory(categoryIndex);
            await StoreAllInCurrentCategory();
        }
    }

    private async Task SelectCategory(int categoryIndex) {
        using var scope = BeginScope(nameof(SelectCategory));
        ErrorIf(!TrySelectCategory(categoryIndex), "Failed to switch category");
        Svc.Log.Debug($"Switching to category {categoryIndex + 1}/{CategoryCount}");
        await WaitUntil(() => IsCategoryReady(categoryIndex), "WaitForCategory");
        await NextFrame(2);
    }

    private async Task StoreAllInCurrentCategory() {
        using var scope = BeginScope(nameof(StoreAllInCurrentCategory));
        while (TryGetNextCabinetId(out var cabinetId)) {
            Svc.Log.Debug($"Storing cabinet item #{cabinetId}");
            ErrorIf(!StoreCabinetItem(cabinetId), "Failed to store item");

            await WaitUntil(() => IsCabinetItemStored(cabinetId) && IsStoreConfirmationClear(), "WaitForStored");
            await NextFrame();
        }
    }

    private static unsafe bool IsCabinetLoaded()
        => UIState.Instance()->Cabinet.IsCabinetLoaded();

    private static unsafe bool TrySelectCategory(int categoryIndex) {
        var addon = Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName);
        var agent = AgentCabinet.Instance();
        if (addon is null || agent is null)
            return false;

        var dropDown = addon->CategoryDropDown;
        if (dropDown is null)
            return false;

        if (IsCategoryReady(categoryIndex))
            return true;

        if (dropDown->GetSelectedItemIndex() != categoryIndex)
            dropDown->SelectItem(categoryIndex);

        var agentCategory = ToAgentCategoryIndex(categoryIndex);
        agent->SelectedCategoryIndex = agentCategory;
        agent->PendingUpdate = true;

        Svc.Log.Debug($"Requested category {categoryIndex}: dropdown={dropDown->GetSelectedItemIndex()}, agent={agent->SelectedCategoryIndex}, addon={addon->CategoryIndex}, pending={agent->PendingUpdate}");
        return true;
    }

    private static unsafe bool IsCategoryReady(int categoryIndex) {
        var addon = Svc.GameGui.GetAddonByName<AddonCabinet>(AddonName);
        var agent = AgentCabinet.Instance();
        if (addon is null || agent is null)
            return false;

        return !agent->PendingUpdate
            && agent->SelectedCategoryIndex == ToAgentCategoryIndex(categoryIndex)
            && addon->CategoryIndex != UninitializedCategoryIndex
            && addon->CategoryIndex == (uint)categoryIndex;
    }

    // agent category ids start at 1; addon dropdown is 0-based
    private static byte ToAgentCategoryIndex(int categoryIndex) => (byte)(categoryIndex + 1);

    private static unsafe bool StoreCabinetItem(uint cabinetId)
        => UIState.Instance()->Cabinet.StoreCabinetItem(cabinetId);

    private static unsafe bool IsCabinetItemStored(uint cabinetId)
        => UIState.Instance()->Cabinet.IsItemInCabinet(cabinetId);

    private static unsafe bool IsStoreConfirmationClear()
        => AgentCabinet.Instance()->ConfirmationAddonId == 0;

    private static unsafe bool TryGetNextCabinetId(out uint cabinetId) {
        cabinetId = 0;

        var numberArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.CabinetStore);
        if (numberArray is null || numberArray->IntArray is null)
            return false;

        var numItems = numberArray->IntArray[0];
        if (numItems == 0)
            return false;

        var items = AgentCabinet.Instance()->Items;
        if (items is null)
            return false;

        ref var cabinet = ref UIState.Instance()->Cabinet;

        for (var i = 0; i < numItems; i++) {
            var cabinetItemIndex = numberArray->IntArray[12 + i * 7];
            var candidateId = items[cabinetItemIndex].Id;

            if (candidateId == 0)
                break;

            if (CabinetSheet.TryGetRow(candidateId, out var row) && !cabinet.IsItemInCabinet(candidateId) && row.Item.Value.Handle is { InGearset: false, IsRepairable: false }) {
                cabinetId = candidateId;
                return true;
            }
        }

        return false;
    }
}
