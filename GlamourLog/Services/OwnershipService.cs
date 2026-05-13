using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Collections.Frozen;
using System.Globalization;

namespace GlamourLog.Services;

internal sealed unsafe class OwnershipService : IDisposable {
    private readonly ArmoireService _armoireService;
    private static readonly Lazy<FrozenDictionary<uint, uint>> CabinetLookup = new(()
        => Cabinet.Where(row => row.Item.RowId != 0)
            .ToFrozenDictionary(row => row.Item.RowId, row => row.RowId));
    private static readonly Lazy<FrozenDictionary<uint, uint>> CabinetByRowLookup = new(()
        => Cabinet.Where(row => row.RowId > 0 && row.Item.RowId != 0)
            .ToFrozenDictionary(row => row.RowId, row => row.Item.RowId));

    public OwnershipService() {
        _armoireService = new ArmoireService();
        _armoireService.ArmoireChanged += OnArmoireChanged;
        Svc.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose() {
        Svc.GameInventory.InventoryChanged -= OnInventoryChanged;
        _armoireService.ArmoireChanged -= OnArmoireChanged;
        _armoireService.Dispose();
    }

    private void OnArmoireChanged() {
        Svc.Get<CatalogService>().OnArmoireChanged();
        Svc.Get<CatalogService>().NotifyDisplayedOwnershipMayHaveChanged();
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events) {
        if (!Svc.ClientState.IsLoggedIn)
            return;

        foreach (var eventData in events) {
            if (!InventoryType.AllPlayer.Contains((InventoryType)eventData.Item.ContainerType))
                continue;
            if (eventData is not InventoryItemAddedArgs)
                continue;

            if (Svc.Get<CatalogService>().GlamourSets.Any(set => set.Items.Contains(eventData.Item.BaseItemId))) {
                Svc.Get<CatalogService>().NotifyDisplayedOwnershipMayHaveChanged();
                return;
            }
        }
    }

    internal bool CanAffordAllMissingGearPieces(GlamourSet glamourSet, HashSet<uint> ownedItems) {
        (uint CostItemId, uint TotalAmount)? firstCost = null;
        uint totalCostQuantity = 0;
        foreach (var itemId in glamourSet.Items) {
            if (ownedItems.Contains(itemId))
                continue;
            var costs = Svc.Get<CatalogService>().CostsLookup.GetItemCosts(itemId);
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
        var ownedCount = GetOwnedPieceCountForSet(glamourSet, ownedItems);
        return ownedCount > 0 && ownedCount < glamourSet.Items.Count;
    }

    internal bool HasContributablePieceInInventory(GlamourSet glamourSet, HashSet<uint> inventoryItemIds)
        => glamourSet.Items.Any(itemId => inventoryItemIds.Contains(itemId) && GetItemStorageState(itemId, glamourSet) is not (ItemStorageState.Armoire or ItemStorageState.DresserSet));

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

    internal HashSet<uint> GetArmoireOwnedItemIds() {
        var owned = new HashSet<uint>();
        foreach (var rawId in _armoireService.GetArmoireItems()) {
            // Some sources emit item ids, others emit cabinet row ids.
            owned.Add(ItemUtil.GetBaseId(rawId).ItemId);
            if (CabinetByRowLookup.Value.TryGetValue(rawId, out var itemId))
                owned.Add(ItemUtil.GetBaseId(itemId).ItemId);
        }
        return owned;
    }

    internal HashSet<uint> GetOwnedItems() {
        var dresserItemIds = GetDresserStoredItemIds();
        var setTokens = Svc.Get<CatalogService>().GlamourSets.Select(s => s.ItemId).ToHashSet();
        HashSet<uint> ownedItems = [.. dresserItemIds.Where(id => !setTokens.Contains(id))];

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
        foreach (var itemId in CabinetLookup.Value.Keys) {
            if (IsInCabinet(itemId))
                ownedItems.Add(itemId);
        }

        return ownedItems;
    }

    internal HashSet<GlamourSet> GetOwnedSets(HashSet<uint> ownedItems) {
        var dresserItemIds = GetDresserStoredItemIds();
        var ownedSets = new HashSet<GlamourSet>();
        foreach (var set in Svc.Get<CatalogService>().GlamourSets) {
            var fullByPieces = GetOwnedPieceCountForSet(set, ownedItems) == set.Items.Count;
            var fullByMirage = dresserItemIds.Contains(set.ItemId) && IsFullMirageOutfit(set);
            if (fullByPieces || fullByMirage)
                ownedSets.Add(set);
        }

        return ownedSets;
    }

    internal ItemStorageState GetItemStorageState(uint itemId, GlamourSet? forSet) {
        if (GetArmoireOwnedItemIds().Contains(itemId) || IsInCabinet(itemId))
            return ItemStorageState.Armoire;

        var dresserItemIds = GetDresserStoredItemIds();
        if (forSet is not null && dresserItemIds.Contains(forSet.ItemId) && forSet.Items.Contains(itemId)) {
            var setRow = MirageStoreSetItem.GetRow(forSet.ItemId);
            return IsPieceInMirageOutfitSlot(setRow, itemId) ? ItemStorageState.DresserSet : ItemStorageState.None;
        }

        return dresserItemIds.Contains(itemId) ? ItemStorageState.DresserLoose : ItemStorageState.None;
    }

    private static bool IsInCabinet(uint itemId) {
        if (!CabinetLookup.Value.TryGetValue(itemId, out var cabinetRowId))
            return false;

        var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        var liveInCabinet = uiState->Cabinet.IsCabinetLoaded() && uiState->Cabinet.IsItemInCabinet(cabinetRowId);

        var itemFinderModule = ItemFinderModule.Instance();
        var bitsetInCabinet = false;
        if (itemFinderModule is not null) {
            var (byteIndex, bitOffset) = Math.DivRem(cabinetRowId - 1048, 32u);
            if (itemFinderModule->CabinetItemUnlockBits.Length > byteIndex)
                bitsetInCabinet = (itemFinderModule->CabinetItemUnlockBits[(int)byteIndex] & (1 << (int)bitOffset)) != 0;
        }

        return liveInCabinet || bitsetInCabinet;
    }

    internal SetStorageState GetSetStorageState(GlamourSet set, HashSet<uint>? ownedItems = null) {
        var effectiveOwned = ownedItems ?? GetOwnedItems();
        if (GetOwnedPieceCountForSet(set, effectiveOwned) != set.Items.Count)
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

    /// <summary> Piece storage as shown in set details (mirage slot, loose dresser, armoire, or none). </summary>
    internal ItemStorageState GetPieceDisplayStorageState(uint itemId, GlamourSet set, SetStorageState setStorageState) {
        var direct = GetItemStorageState(itemId, set);
        if (direct is not ItemStorageState.None)
            return direct;

        return setStorageState switch {
            SetStorageState.Armoire => ItemStorageState.Armoire,
            SetStorageState.Dresser => ItemStorageState.DresserSet,
            SetStorageState.Mixed when GetItemStorageState(itemId, null) is ItemStorageState.Armoire => ItemStorageState.Armoire,
            SetStorageState.Mixed => ItemStorageState.DresserSet,
            _ => ItemStorageState.None,
        };
    }

    /// <summary> True when the set list or any piece row would show the armoire (dresser) misplacement badge. </summary>
    internal bool SetHasArmoireMisplacementWarning(GlamourSet set, HashSet<uint> ownedItems, HashSet<uint> armoireItemIds) {
        var setStorageState = GetSetStorageState(set, ownedItems);
        if (setStorageState is SetStorageState.Dresser && set.Items.Any(armoireItemIds.Contains))
            return true;
        foreach (var itemId in set.Items) {
            var pieceState = GetPieceDisplayStorageState(itemId, set, setStorageState);
            if (pieceState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose && armoireItemIds.Contains(itemId))
                return true;
        }

        return false;
    }

    internal int GetOwnedPieceCountForSet(GlamourSet set, HashSet<uint>? ownedItems = null) {
        var effectiveOwned = ownedItems ?? GetOwnedItems();
        var count = 0;
        var hasSetToken = GetDresserStoredItemIds().Contains(set.ItemId);
        var setRow = MirageStoreSetItem.GetRow(set.ItemId);

        foreach (var itemId in set.Items) {
            if (effectiveOwned.Contains(itemId)) {
                count++;
                continue;
            }

            if (!hasSetToken)
                continue;

            if (IsPieceInMirageOutfitSlot(setRow, itemId))
                count++;
        }

        return count;
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

    /// <summary> True when the sheet lists the same number of pieces as <paramref name="set"/> and each slot is collected on the mirage outfit (see <see cref="MirageStoreSetItemExtensions.IsFullSetCollected(MirageStoreSetItem, bool)"/>). </summary>
    private static bool IsFullMirageOutfit(GlamourSet set) {
        var row = MirageStoreSetItem.GetRow(set.ItemId);
        if (!row.IsFullSetCollected())
            return false;

        var defined = 0;
        foreach (var itemRef in row.Items) {
            if (itemRef.RowId != 0)
                defined++;
        }

        return defined == set.Items.Count;
    }

    private static bool IsPieceInMirageOutfitSlot(MirageStoreSetItem row, uint pieceItemId) {
        var itemIndex = 0;
        foreach (var itemRef in row.Items) {
            if (itemRef.RowId != 0 && itemRef.RowId == pieceItemId && row.IsSetSlotCollected(itemIndex))
                return true;
            itemIndex++;
        }

        return false;
    }

    internal void GetLalaAchievementsExportBuckets(out Dictionary<string, uint[]> outfitsBySetId, out uint[] armoires) {
        var dresserIds = GetDresserStoredItemIds();
        var outfitsBuilder = new Dictionary<string, HashSet<uint>>();

        foreach (var set in Svc.Get<CatalogService>().GlamourSets) {
            if (set.ItemId == 0 || !dresserIds.Contains(set.ItemId))
                continue;
            var setRow = MirageStoreSetItem.GetRow(set.ItemId);
            var pieces = new HashSet<uint>();
            foreach (var pieceId in set.Items) {
                if (pieceId == 0)
                    continue;
                if (IsPieceInMirageOutfitSlot(setRow, pieceId))
                    pieces.Add(pieceId);
            }

            if (pieces.Count > 0)
                outfitsBuilder[set.ItemId.ToString()] = pieces;
        }

        outfitsBySetId = [];
        foreach (var key in outfitsBuilder.Keys.OrderBy(k => uint.Parse(k, CultureInfo.InvariantCulture)))
            outfitsBySetId[key] = [.. outfitsBuilder[key].OrderBy(x => x)];

        var armoireSet = new HashSet<uint>();
        foreach (var id in GetArmoireOwnedItemIds()) {
            if (id != 0)
                armoireSet.Add(id);
        }

        foreach (var itemId in CabinetLookup.Value.Keys) {
            if (itemId == 0)
                continue;
            if (IsInCabinet(itemId))
                armoireSet.Add(itemId);
        }

        armoires = [.. armoireSet.OrderBy(x => x)];
    }
}
