using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace GlamourLog.Services;

internal sealed unsafe class OwnershipService : IDisposable {
    private readonly ArmoireService _armoireService;

    public OwnershipService() {
        _armoireService = new ArmoireService();
        _armoireService.ArmoireChanged += OnArmoireChanged;
    }

    public void Dispose() {
        _armoireService.ArmoireChanged -= OnArmoireChanged;
        _armoireService.Dispose();
    }

    private void OnArmoireChanged() {
        Svc.Catalog.OnArmoireChanged();
        Svc.Catalog.NotifyDisplayedOwnershipMayHaveChanged();
    }

    internal bool CanAffordAllMissingGearPieces(GlamourSet glamourSet, HashSet<uint> ownedItems) {
        (uint CostItemId, uint TotalAmount)? firstCost = null;
        uint totalCostQuantity = 0;
        foreach (var itemId in glamourSet.Items) {
            if (ownedItems.Contains(itemId))
                continue;
            var costs = Svc.Catalog.CostsLookup.GetItemCosts(itemId);
            if (costs.Count == 0)
                return false;
            var cost = costs[0];
            firstCost ??= (cost.ItemId, 0);
            if (firstCost.Value.CostItemId != cost.ItemId)
                return false;
            totalCostQuantity += cost.Amount;
        }

        if (firstCost == null)
            return false;
        var ownedCount = CurrencyManager.Instance()->SpecialItemBucket.TryGetValue(firstCost.Value.CostItemId, out var value, true)
            ? (int)value.Count
            : InventoryManager.Instance()->GetInventoryItemCount(firstCost.Value.CostItemId);
        return totalCostQuantity <= ownedCount;
    }

    internal bool IsPartiallyCompleted(GlamourSet glamourSet, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        if (ownedSets.Contains(glamourSet))
            return false;
        var ownedCount = glamourSet.Items.Count(ownedItems.Contains);
        return ownedCount > 0 && ownedCount < glamourSet.Items.Count;
    }

    internal bool IsDoneButNotInDresser(GlamourSet glamourSet, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        if (ownedSets.Contains(glamourSet))
            return false;
        var ownedCount = glamourSet.Items.Count(ownedItems.Contains);
        return ownedCount == glamourSet.Items.Count;
    }

    internal bool IsMarketboardPurchasable(GlamourSet glamourSet)
        => glamourSet.Items.Any(itemId => !Item.GetRow(itemId).IsUntradable);

    internal HashSet<uint> GetDresserStoredItemIds() {
        var dresserItemIds = new HashSet<uint>();
        if (ItemFinderModule.Instance() is null)
            return dresserItemIds;
        foreach (var dresserItemId in ItemFinderModule.Instance()->GlamourDresserBaseItemIds)
            dresserItemIds.Add(dresserItemId);
        return dresserItemIds;
    }

    internal HashSet<uint> GetArmoireOwnedItemIds() => _armoireService.GetArmoireItems();

    internal HashSet<uint> GetOwnedItems() {
        var dresserItemIds = GetDresserStoredItemIds();
        HashSet<uint> ownedItems = [.. dresserItemIds];

        foreach (var set in Svc.Catalog.GlamourSets) {
            if (!dresserItemIds.Contains(set.ItemId))
                continue;
            foreach (var setItemId in set.Items)
                ownedItems.Add(setItemId);
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null) {
            foreach (var inventoryType in InventoryType.AllPlayer) {
                var inventoryContainer = inventoryManager->GetInventoryContainer(inventoryType);
                if (inventoryContainer == null)
                    continue;
                for (var i = 0; i < inventoryContainer->Size; ++i) {
                    var item = inventoryContainer->GetInventorySlot(i);
                    if (item != null && item->ItemId != 0)
                        ownedItems.Add(ItemUtil.GetBaseId(item->ItemId).ItemId);
                }
            }
        }
        ownedItems.UnionWith(GetArmoireOwnedItemIds());
        return ownedItems;
    }

    internal HashSet<GlamourSet> GetOwnedSets(HashSet<uint> ownedItems)
        => [.. Svc.Catalog.GlamourSets.Where(set => GetDresserStoredItemIds().Contains(set.ItemId) || set.Items.All(ownedItems.Contains))];

    internal ItemStorageState GetItemStorageState(uint itemId, GlamourSet? forSet) {
        if (GetArmoireOwnedItemIds().Contains(itemId))
            return ItemStorageState.Armoire;

        var dresserItemIds = GetDresserStoredItemIds();
        if (forSet is not null && dresserItemIds.Contains(forSet.ItemId) && forSet.Items.Contains(itemId))
            return ItemStorageState.DresserSet;

        if (dresserItemIds.Contains(itemId))
            return ItemStorageState.DresserLoose;

        return ItemStorageState.None;
    }

    internal SetStorageState GetSetStorageState(GlamourSet set, HashSet<uint>? ownedItems = null) {
        var effectiveOwned = ownedItems ?? GetOwnedItems();
        if (!set.Items.All(effectiveOwned.Contains))
            return SetStorageState.None;

        var hasArmoire = false;
        var hasDresserSet = false;
        var hasDresserLoose = false;
        foreach (var itemId in set.Items) {
            switch (GetItemStorageState(itemId, set)) {
                case ItemStorageState.Armoire:
                    hasArmoire = true;
                    break;
                case ItemStorageState.DresserSet:
                    hasDresserSet = true;
                    break;
                case ItemStorageState.DresserLoose:
                    hasDresserLoose = true;
                    break;
            }
        }

        if (hasDresserLoose)
            return SetStorageState.None;
        if (hasArmoire && hasDresserSet)
            return SetStorageState.Mixed;
        if (hasArmoire)
            return SetStorageState.Armoire;
        if (hasDresserSet)
            return SetStorageState.Dresser;
        return SetStorageState.None;
    }

    // TODO: use inventorymanager/inventorytype extensions
    internal HashSet<uint> GetInventoryItemsOnly() {
        HashSet<uint> ownedItems = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null) {
            foreach (var inventoryType in InventoryType.AllPlayer) {
                var inventoryContainer = inventoryManager->GetInventoryContainer(inventoryType);
                if (inventoryContainer == null)
                    continue;
                for (var i = 0; i < inventoryContainer->Size; ++i) {
                    var item = inventoryContainer->GetInventorySlot(i);
                    if (item != null && item->ItemId != 0)
                        ownedItems.Add(ItemUtil.GetBaseId(item->ItemId).ItemId);
                }
            }
        }
        return ownedItems;
    }

    // TODO: make ContainsAny extensions that take predicates
    internal bool IsItemInGlamourDresser(uint itemId, GlamourSet? forSet) {
        if (ItemFinderModule.Instance() is null)
            return false;
        foreach (var id in ItemFinderModule.Instance()->GlamourDresserBaseItemIds) {
            if (id == itemId)
                return true;
            if (forSet != null && id == forSet.ItemId)
                return true;
        }
        return false;
    }
}
