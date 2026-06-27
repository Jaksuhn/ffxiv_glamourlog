using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using Lumina.Excel.Sheets;

namespace GlamourLog.Features.PrismBox;

/// <summary>
/// Reads crystallize agent data and inventory for store automation.
/// Category readiness uses the filter handler's native-load hook (ATK tree item count is 0 while filtered).
/// </summary>
internal sealed class DresserStoreScanner {
    private const string Crystallize = "MiragePrismPrismBoxCrystallize";
    private const int MaxCategoryItems = 140;

    private readonly IPrismBoxRowFilter[] _filters;
    private readonly HashSet<uint> _pendingStoredBaseIds = [];

    internal DresserStoreScanner() {
        _filters = [
            new HideDresserDepositedFilter(),
            new HideArmoireEligibleFilter(),
            new HideNonOutfitItemsFilter(),
        ];
    }

    internal unsafe bool TrySelectCategory(int categoryIndex) {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(Crystallize);
        var data = GetData();
        if (addon is null || data is null)
            return false;

        var nextBtn = addon->GetComponentButtonById(7);
        var dropDown = FindCategoryDropDown(addon);
        if (nextBtn is null || dropDown is null)
            return false;

        if (IsCategoryTabAligned(categoryIndex) && IsCategoryReady(categoryIndex))
            return true;

        data->CrystallizeCategory = categoryIndex;
        ClearAgentBufferTail(data);

        if (dropDown->GetSelectedItemIndex() != categoryIndex)
            nextBtn->Click();
        else
            addon->OnRefresh(0, null);

        Svc.Log.Debug(
            $"[DresserStore] requested category {categoryIndex}: dropdown={dropDown->GetSelectedItemIndex()}, agent={data->CrystallizeCategory}");
        return true;
    }

    internal unsafe bool IsCategoryTabAligned(int categoryIndex) {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(Crystallize);
        var data = GetData();
        if (addon is null || data is null)
            return false;

        var dropDown = FindCategoryDropDown(addon);
        if (dropDown is null)
            return false;

        return data->CrystallizeCategory == categoryIndex
            && dropDown->GetSelectedItemIndex() == categoryIndex;
    }

    internal unsafe bool IsCategoryReady(int categoryIndex) {
        if (!IsCategoryTabAligned(categoryIndex))
            return false;

        var handler = Svc.Get<CrystallizeListHandler>();
        if (!handler.IsCategoryUsableForStore(categoryIndex))
            return false;

        var data = GetData();
        if (data is null)
            return false;

        var reported = (int)data->CrystallizeItemCount;
        var scanned = ScanPopulatedCategoryItemCount(data);
        Svc.Log.Debug($"[DresserStore] category {categoryIndex} ready: agent={reported} scanned={scanned}");
        return true;
    }

    internal void MarkStored(IEnumerable<uint> itemIds) {
        foreach (var itemId in itemIds) {
            var baseId = ItemUtil.GetBaseId(itemId).ItemId;
            if (baseId != 0)
                _pendingStoredBaseIds.Add(baseId);
        }

        PrunePendingStored();
    }

    internal unsafe bool TryGetNextTarget(out PrismBoxCrystallizeItem item) {
        item = default;
        PrunePendingStored();

        var data = GetData();
        if (data is null)
            return false;

        var handler = Svc.Get<CrystallizeListHandler>();
        var categoryIndex = data->CrystallizeCategory;

        if (handler.TryGetNextVisibleStorableFromSnapshot(categoryIndex, IsStorableRow, out item)) {
            Svc.Log.Debug($"[DresserStore] target from filter snapshot in category {categoryIndex}");
            return true;
        }

        var count = InferPopulatedCategoryItemCount(data);
        for (var i = 0; i < count; i++) {
            var row = data->CrystallizeItems[i];
            if (row.ItemId == 0)
                break;

            if (!IsStorableRow(row))
                continue;

            item = row;
            DresserStore.RefreshInventoryLocation(ref item);
            Svc.Log.Debug($"[DresserStore] target from agent buffer in category {categoryIndex}");
            return true;
        }

        if (count > 0)
            Svc.Log.Debug($"[DresserStore] no storable target in category {categoryIndex} ({count} agent row(s))");

        return false;
    }

    internal unsafe bool TryGetStorablePiece(uint itemId, out PrismBoxCrystallizeItem row) {
        row = default;
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0)
            return false;

        var data = GetData();
        var handler = Svc.Get<CrystallizeListHandler>();
        var categoryIndex = data is not null ? data->CrystallizeCategory : -1;

        if (categoryIndex >= 0
            && handler.TryFindSnapshotRow(categoryIndex, itemId, out var snapshotRow)
            && IsStorableRow(snapshotRow)) {
            row = snapshotRow;
            DresserStore.RefreshInventoryLocation(ref row);
            return true;
        }

        if (data is not null) {
            var count = InferPopulatedCategoryItemCount(data);
            for (var i = 0; i < count; i++) {
                var candidate = data->CrystallizeItems[i];
                if (candidate.ItemId == 0)
                    break;

                if (ItemUtil.GetBaseId(candidate.ItemId).ItemId != baseId)
                    continue;

                if (!IsStorableRow(candidate))
                    continue;

                row = candidate;
                DresserStore.RefreshInventoryLocation(ref row);
                return true;
            }
        }

        return TryResolveFromInventory(itemId, out row);
    }

    internal List<PrismBoxCrystallizeItem> CollectStorablePiecesInSet(MirageStoreSetItem setRow) {
        var results = new List<PrismBoxCrystallizeItem>();
        foreach (var piece in setRow.Items) {
            if (piece.RowId == 0)
                continue;

            if (!TryGetStorablePiece(piece.RowId, out var row))
                continue;

            results.Add(row);
        }

        return results;
    }

    private bool TryResolveFromInventory(uint itemId, out PrismBoxCrystallizeItem row) {
        row = default;
        var handle = (ItemHandle)itemId;
        if (!handle.TrySetItemLocation())
            return false;

        row = new PrismBoxCrystallizeItem {
            ItemId = itemId,
            Inventory = handle.ItemLocation.Container,
            Slot = handle.ItemLocation.Slot,
        };
        return IsStorableRow(row);
    }

    private bool IsStorableRow(PrismBoxCrystallizeItem row) {
        if (row.ItemId == 0)
            return false;

        var itemId = ItemUtil.GetBaseId(row.ItemId).ItemId;
        if (itemId == 0 || !MirageStoreSetItemLookup.TryGetRow(itemId, out _))
            return false;

        if (_pendingStoredBaseIds.Contains(itemId))
            return false;

        if (ShouldExclude(itemId))
            return false;

        var handle = (ItemHandle)row.ItemId;
        if (row.Inventory == InventoryType.Invalid) {
            if (!handle.TrySetItemLocation())
                return false;
            return true;
        }

        return handle.TrySetItemLocation();
    }

    private bool ShouldExclude(uint baseId)
        => _filters.Any(f => f.IsEnabled && f.ShouldHide(baseId));

    private void PrunePendingStored() {
        if (_pendingStoredBaseIds.Count == 0)
            return;

        var ownership = Svc.Get<OwnershipService>();
        _pendingStoredBaseIds.RemoveWhere(ownership.IsCrystallizeItemFullyDeposited);
    }

    private static unsafe void ClearAgentBufferTail(MiragePrismPrismBoxData* data) {
        var reported = data->CrystallizeItemCount;
        for (var i = reported; i < MaxCategoryItems; i++)
            data->CrystallizeItems[i] = default;
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

    private static unsafe int InferPopulatedCategoryItemCount(MiragePrismPrismBoxData* data)
        => ScanPopulatedCategoryItemCount(data);

    private static unsafe int ScanPopulatedCategoryItemCount(MiragePrismPrismBoxData* data) {
        var lastIndex = -1;
        for (var i = 0; i < MaxCategoryItems; i++) {
            if (data->CrystallizeItems[i].ItemId != 0)
                lastIndex = i;
        }

        if (lastIndex >= 0)
            return lastIndex + 1;

        return data->CrystallizeItemCount > 0 ? data->CrystallizeItemCount : 0;
    }

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }
}
