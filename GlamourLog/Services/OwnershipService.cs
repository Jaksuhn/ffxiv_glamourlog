using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Collections.Frozen;
using System.Globalization;

namespace GlamourLog.Services;

internal readonly struct OwnershipSnapshot {
    internal HashSet<uint> DresserItemIds { get; init; }
    internal HashSet<uint> ArmoireOwnedItemIds { get; init; }
    internal HashSet<uint> StorageOwnedItems { get; init; }
    internal HashSet<uint> OwnedItems { get; init; }
    internal HashSet<uint> InventoryItemIds { get; init; }
}

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

    internal event System.Action? ArmoireOwnershipChanged;

    private void OnArmoireChanged() {
        Svc.Get<CatalogService>().OnArmoireChanged();
        Svc.Get<CatalogService>().NotifyDisplayedOwnershipMayHaveChanged();
        ArmoireOwnershipChanged?.Invoke();
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events) {
        if (!Svc.ClientState.IsLoggedIn)
            return;

        foreach (var eventData in events) {
            if (!InventoryType.AllPlayer.Contains((InventoryType)eventData.Item.ContainerType))
                continue;
            if (eventData is not (InventoryItemAddedArgs or InventoryItemRemovedArgs))
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

    internal bool IsPartiallyCompleted(GlamourSet glamourSet, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems, OwnershipSnapshot snap) {
        if (ownedSets.Contains(glamourSet))
            return false;
        var ownedCount = GetOwnedPieceCountForSet(glamourSet, ownedItems, snap);
        return ownedCount > 0 && ownedCount < glamourSet.Items.Count;
    }

    internal bool IsPartiallyCompleted(GlamourSet glamourSet, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        var snap = CaptureSnapshot();
        return IsPartiallyCompleted(glamourSet, ownedSets, ownedItems, snap);
    }

    internal bool HasContributablePieceInInventory(GlamourSet glamourSet, HashSet<uint> inventoryItemIds, OwnershipSnapshot snap)
        => glamourSet.Items.Any(itemId => inventoryItemIds.Contains(itemId) && GetItemStorageState(itemId, glamourSet, snap) is not (ItemStorageState.Armoire or ItemStorageState.DresserSet));

    internal bool HasContributablePieceInInventory(GlamourSet glamourSet, HashSet<uint> inventoryItemIds) {
        var snap = CaptureSnapshot();
        return HasContributablePieceInInventory(glamourSet, inventoryItemIds, snap);
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

    /// <summary> Dresser / armoire / glamour cabinet (not player inventories). </summary>
    private HashSet<uint> BuildStorageOwnedItems(HashSet<uint> dresserItemIds, HashSet<uint> armoireOwned) {
        var setTokens = Svc.Get<CatalogService>().GlamourSets.Select(s => s.ItemId).ToHashSet();
        HashSet<uint> owned = [.. dresserItemIds.Where(id => !setTokens.Contains(id))];
        owned.UnionWith(armoireOwned);
        foreach (var itemId in CabinetLookup.Value.Keys) {
            if (IsInCabinet(itemId))
                owned.Add(itemId);
        }

        return owned;
    }

    private HashSet<uint> ScanInventoryItemIds() {
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

    /// <summary> One scan each of dresser, armoire, glamour cabinet, and player inventories. </summary>
    internal OwnershipSnapshot CaptureSnapshot() {
        var dresser = GetDresserStoredItemIds();
        var armoire = GetArmoireOwnedItemIds();
        var storage = BuildStorageOwnedItems(dresser, armoire);
        var inventory = ScanInventoryItemIds();
        var owned = new HashSet<uint>(storage);
        owned.UnionWith(inventory);
        return new OwnershipSnapshot {
            DresserItemIds = dresser,
            ArmoireOwnedItemIds = armoire,
            StorageOwnedItems = storage,
            OwnedItems = owned,
            InventoryItemIds = inventory,
        };
    }

    /// <summary>Owned in glamour dresser (loose), armoire, or glamour cabinet — not player inventory.</summary>
    internal HashSet<uint> GetStorageOwnedItems() {
        var dresserItemIds = GetDresserStoredItemIds();
        var armoire = GetArmoireOwnedItemIds();
        return BuildStorageOwnedItems(dresserItemIds, armoire);
    }

    internal HashSet<uint> GetOwnedItems() {
        var snap = CaptureSnapshot();
        return snap.OwnedItems;
    }

    /// <summary>Checkmark / completed sets: storage only, or a full mirage plate on the dresser.</summary>
    internal bool IsSetCompleted(GlamourSet set, OwnershipSnapshot snap) {
        if (snap.DresserItemIds.Contains(set.ItemId) && IsFullMirageOutfit(set))
            return true;

        return GetOwnedPieceCountForSet(set, snap.StorageOwnedItems, snap) == set.Items.Count;
    }

    internal bool IsSetCompleted(GlamourSet set) {
        var snap = CaptureSnapshot();
        return IsSetCompleted(set, snap);
    }

    internal HashSet<GlamourSet> GetOwnedSets(OwnershipSnapshot snap) {
        var ownedSets = new HashSet<GlamourSet>();
        foreach (var set in Svc.Get<CatalogService>().GlamourSets) {
            if (IsSetCompleted(set, snap))
                ownedSets.Add(set);
        }

        return ownedSets;
    }

    internal ItemStorageState GetItemStorageState(uint itemId, GlamourSet? forSet, OwnershipSnapshot snap) {
        if (snap.ArmoireOwnedItemIds.Contains(itemId) || IsInCabinet(itemId))
            return ItemStorageState.Armoire;

        var dresserItemIds = snap.DresserItemIds;
        if (forSet is not null && dresserItemIds.Contains(forSet.ItemId) && forSet.Items.Contains(itemId)) {
            var setRow = MirageStoreSetItem.GetRow(forSet.ItemId);
            return IsPieceInMirageOutfitSlot(setRow, itemId) ? ItemStorageState.DresserSet : ItemStorageState.None;
        }

        return dresserItemIds.Contains(itemId) ? ItemStorageState.DresserLoose : ItemStorageState.None;
    }

    internal ItemStorageState GetItemStorageState(uint itemId, GlamourSet? forSet) {
        var snap = CaptureSnapshot();
        return GetItemStorageState(itemId, forSet, snap);
    }

    internal uint GetItemIdFromLookups(uint cacheOrEntryId) {
        if (cacheOrEntryId == 0)
            return 0;

        var baseId = ItemUtil.GetBaseId(cacheOrEntryId).ItemId;
        if (CabinetLookup.Value.ContainsKey(baseId))
            return baseId;

        if (CabinetByRowLookup.Value.TryGetValue(cacheOrEntryId, out var fromEntry))
            return ItemUtil.GetBaseId(fromEntry).ItemId;

        if (CabinetByRowLookup.Value.TryGetValue(baseId, out var fromBase))
            return ItemUtil.GetBaseId(fromBase).ItemId;

        return baseId;
    }

    internal bool IsItemInArmoire(uint itemId) {
        itemId = GetItemIdFromLookups(itemId);
        if (itemId == 0)
            return false;
        if (GetArmoireOwnedItemIds().Contains(itemId))
            return true;
        return IsInCabinet(itemId);
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

    internal SetStorageState GetSetStorageState(GlamourSet set, OwnershipSnapshot snap) {
        if (!IsSetCompleted(set, snap))
            return SetStorageState.None;

        var hasArmoire = false;
        var hasDresserSet = false;
        var hasDresserLoose = false;
        foreach (var itemId in set.Items) {
            switch (GetItemStorageState(itemId, set, snap)) {
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

    internal SetStorageState GetSetStorageState(GlamourSet set, HashSet<uint>? ownedItems = null) {
        _ = ownedItems;
        return GetSetStorageState(set, CaptureSnapshot());
    }

    /// <summary> Piece storage as shown in set details (mirage slot, loose dresser, armoire, or none). </summary>
    internal ItemStorageState GetPieceDisplayStorageState(uint itemId, GlamourSet set, SetStorageState setStorageState, OwnershipSnapshot snap) {
        var direct = GetItemStorageState(itemId, set, snap);
        if (direct is not ItemStorageState.None)
            return direct;

        return setStorageState switch {
            SetStorageState.Armoire => ItemStorageState.Armoire,
            SetStorageState.Dresser => ItemStorageState.DresserSet,
            SetStorageState.Mixed when GetItemStorageState(itemId, null, snap) is ItemStorageState.Armoire => ItemStorageState.Armoire,
            SetStorageState.Mixed => ItemStorageState.DresserSet,
            _ => ItemStorageState.None,
        };
    }

    internal ItemStorageState GetPieceDisplayStorageState(uint itemId, GlamourSet set, SetStorageState setStorageState) {
        var snap = CaptureSnapshot();
        return GetPieceDisplayStorageState(itemId, set, setStorageState, snap);
    }

    /// <summary> True when the set list or any piece row would show the armoire (dresser) misplacement badge. </summary>
    internal bool SetHasArmoireMisplacementWarning(GlamourSet set, HashSet<uint> ownedItems, HashSet<uint> armoireItemIds, OwnershipSnapshot snap) {
        _ = ownedItems;
        var setStorageState = GetSetStorageState(set, snap);
        if (setStorageState is SetStorageState.Dresser && set.Items.Any(armoireItemIds.Contains))
            return true;
        foreach (var itemId in set.Items) {
            var pieceState = GetPieceDisplayStorageState(itemId, set, setStorageState, snap);
            if (pieceState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose && armoireItemIds.Contains(itemId))
                return true;
        }

        return false;
    }

    internal bool SetHasArmoireMisplacementWarning(GlamourSet set, HashSet<uint> ownedItems, HashSet<uint> armoireItemIds) {
        var snap = CaptureSnapshot();
        return SetHasArmoireMisplacementWarning(set, ownedItems, armoireItemIds, snap);
    }

    internal int GetOwnedPieceCountForSet(GlamourSet set, HashSet<uint> effectiveOwned, OwnershipSnapshot snap) {
        var count = 0;
        var hasSetToken = snap.DresserItemIds.Contains(set.ItemId);
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

    internal int GetOwnedPieceCountForSet(GlamourSet set, HashSet<uint>? ownedItems = null) {
        var snap = CaptureSnapshot();
        var effectiveOwned = ownedItems ?? snap.OwnedItems;
        return GetOwnedPieceCountForSet(set, effectiveOwned, snap);
    }

    // TODO: use inventorymanager/inventorytype extensions
    internal HashSet<uint> GetInventoryItemsOnly() => ScanInventoryItemIds();

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
