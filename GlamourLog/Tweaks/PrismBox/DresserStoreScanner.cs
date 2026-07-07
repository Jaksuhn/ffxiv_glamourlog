using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Services;

namespace GlamourLog.Features.PrismBox;

/// <summary>
/// Reads the active crystallize category and inventory state for direct dresser-store automation.
/// </summary>
internal sealed class DresserStoreScanner {
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

        var count = ScanPopulatedCategoryItemCount(data);
        for (var i = 0; i < count; i++) {
            var row = data->CrystallizeItems[i];
            if (row.ItemId == 0)
                break;

            if (!IsStorableRow(row))
                continue;

            item = row;
            DresserStore.RefreshInventoryLocation(ref item);
            return true;
        }

        return false;
    }

    internal unsafe bool TryGetStorablePiece(uint itemId, out PrismBoxCrystallizeItem row) {
        row = default;
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0)
            return false;

        var data = GetData();
        if (data is not null) {
            var count = ScanPopulatedCategoryItemCount(data);
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
