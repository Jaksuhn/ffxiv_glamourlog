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
    internal HashSet<GlamourSet> OwnedSets { get; init; }
    internal HashSet<uint> ArmoireCatalogItemIds { get; init; }
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
        Svc.Get<CatalogService>().NotifyOwnershipChanged();
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

            var itemId = eventData.Item.BaseItemId;
            var catalog = Svc.Get<CatalogService>();
            if (catalog.GlamourSets.Any(set => set.Items.Contains(itemId)) || catalog.IsKnownCostCurrency(itemId)) {
                catalog.NotifyOwnershipChanged();
                return;
            }
        }
    }

    internal bool CanAffordAllMissingGearPieces(GlamourSet glamourSet, OwnershipSnapshot snap) {
        (uint CostItemId, uint TotalAmount)? firstCost = null;
        uint totalCostQuantity = 0;
        foreach (var itemId in glamourSet.Items) {
            if (snap.OwnedItems.Contains(itemId))
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

    internal bool IsPartiallyCompleted(GlamourSet glamourSet, OwnershipSnapshot snap) {
        if (snap.OwnedSets.Contains(glamourSet))
            return false;
        var ownedCount = GetOwnedPieceCountForSet(glamourSet, snap);
        return ownedCount > 0 && ownedCount < glamourSet.Items.Count;
    }

    internal bool HasContributablePieceInInventory(GlamourSet glamourSet, OwnershipSnapshot snap)
        => glamourSet.Items.Any(itemId => snap.InventoryItemIds.Contains(itemId)
            && GetItemStorageState(itemId, snap, glamourSet) is not (ItemStorageState.Armoire or ItemStorageState.DresserSet));

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
        var setTokens = Svc.Get<CatalogService>().GlamourSets.Where(s => !s.NonSetCabinetPiece).Select(s => s.ItemId).ToHashSet();
        HashSet<uint> owned = [.. dresserItemIds.Where(id => !setTokens.Contains(id))];
        owned.UnionWith(armoireOwned);
        foreach (var itemId in CabinetLookup.Value.Keys) {
            if (IsInCabinet(itemId))
                owned.Add(itemId);
        }

        return owned;
    }

    /// <summary> One scan each of dresser, armoire, glamour cabinet, and player inventories. </summary>
    internal OwnershipSnapshot CaptureSnapshot() {
        var dresser = GetDresserStoredItemIds();
        var armoire = GetArmoireOwnedItemIds();
        var storage = BuildStorageOwnedItems(dresser, armoire);
        var inventory = InventoryType.AllPlayer.SelectMany(inv => inv.Items.Where(i => i.ItemId != 0)).Select(item => item.BaseItemId).ToHashSet();
        var owned = new HashSet<uint>(storage);
        owned.UnionWith(inventory);
        var snap = new OwnershipSnapshot {
            DresserItemIds = dresser,
            ArmoireOwnedItemIds = armoire,
            StorageOwnedItems = storage,
            OwnedItems = owned,
            InventoryItemIds = inventory,
            ArmoireCatalogItemIds = Svc.Get<CatalogService>().ArmoireItemIds,
        };
        var ownedSets = Svc.Get<CatalogService>().GlamourSets.Where(s => IsSetCompleted(s, snap)).ToHashSet();
        return snap with { OwnedSets = ownedSets };
    }

    /// <summary>Checkmark / completed sets: storage only, or a full mirage plate on the dresser.</summary>
    internal bool IsSetCompleted(GlamourSet set, OwnershipSnapshot snap) {
        if (set.NonSetCabinetPiece)
            return set.Items.Count > 0 && set.Items.All(snap.StorageOwnedItems.Contains);

        if (snap.DresserItemIds.Contains(set.ItemId) && IsFullMirageOutfit(set))
            return true;

        return GetOwnedPieceCountForSet(set, snap, storageOnly: true) == set.Items.Count;
    }

    internal ItemStorageState GetItemStorageState(uint itemId, OwnershipSnapshot snap, GlamourSet? mirageContext = null) {
        if (snap.ArmoireOwnedItemIds.Contains(itemId) || IsInCabinet(itemId))
            return ItemStorageState.Armoire;

        if (mirageContext is { NonSetCabinetPiece: false } set && set.Items.Contains(itemId)) {
            return IsPieceInMirageOutfitSlot(MirageStoreSetItem.GetRow(set.ItemId), itemId) ? ItemStorageState.DresserSet : ItemStorageState.None;
        }

        if (IsPieceInMirageOutfitSlot(itemId))
            return ItemStorageState.DresserSet;

        return snap.DresserItemIds.Contains(itemId) ? ItemStorageState.DresserLoose : ItemStorageState.None;
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

    /// <summary> True when the item can be stored in the armoire (Cabinet sheet), regardless of deposit state. </summary>
    internal bool IsArmoireEligible(uint itemId) {
        itemId = GetItemIdFromLookups(itemId);
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        return itemId != 0 && CabinetLookup.Value.ContainsKey(itemId);
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
            switch (GetItemStorageState(itemId, snap, set)) {
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
    internal ItemStorageState GetPieceDisplayStorageState(uint itemId, GlamourSet set, SetStorageState setStorageState, OwnershipSnapshot snap) {
        var direct = GetItemStorageState(itemId, snap, set);
        if (direct is not ItemStorageState.None)
            return direct;

        return setStorageState switch {
            SetStorageState.Armoire => ItemStorageState.Armoire,
            SetStorageState.Dresser => ItemStorageState.DresserSet,
            SetStorageState.Mixed when GetItemStorageState(itemId, snap) is ItemStorageState.Armoire => ItemStorageState.Armoire,
            SetStorageState.Mixed => ItemStorageState.DresserSet,
            _ => ItemStorageState.None,
        };
    }

    /// <summary> True when the set list or any piece row would show the armoire (dresser) misplacement badge. </summary>
    internal bool SetHasArmoireMisplacementWarning(GlamourSet set, OwnershipSnapshot snap) {
        var setStorageState = GetSetStorageState(set, snap);
        if (setStorageState is SetStorageState.Dresser && set.Items.Any(snap.ArmoireCatalogItemIds.Contains))
            return true;
        foreach (var itemId in set.Items) {
            var pieceState = GetPieceDisplayStorageState(itemId, set, setStorageState, snap);
            if (pieceState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose && snap.ArmoireCatalogItemIds.Contains(itemId))
                return true;
        }

        return false;
    }

    internal int GetOwnedPieceCountForSet(GlamourSet set, OwnershipSnapshot snap, bool storageOnly = false) {
        var effectiveOwned = storageOnly ? snap.StorageOwnedItems : snap.OwnedItems;
        if (set.NonSetCabinetPiece)
            return set.Items.Count(effectiveOwned.Contains);

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

    internal bool IsItemInGlamourDresser(uint itemId) {
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        if (itemId == 0)
            return false;

        if (ItemFinderModule.Instance() is null)
            return false;

        foreach (var id in ItemFinderModule.Instance()->GlamourDresserBaseItemIds) {
            if (id == itemId)
                return true;
        }

        return IsPieceInMirageOutfitSlot(itemId);
    }

    /// <summary>
    /// Crystallize picker: hide when loose in the dresser, or when every mirage outfit that includes
    /// this piece already has it deposited. Still show when at least one outfit set can accept it.
    /// </summary>
    internal bool IsCrystallizeItemFullyDeposited(uint itemId) {
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        if (itemId == 0)
            return false;

        var itemFinder = ItemFinderModule.Instance();
        if (itemFinder is not null) {
            foreach (var id in itemFinder->GlamourDresserBaseItemIds) {
                if (id == itemId)
                    return true;
            }
        }

        var inAnyOutfit = false;
        foreach (var row in MirageStoreSetItem.Where(r => r.RowId > 0)) {
            if (!IsPieceInOutfitDefinition(row, itemId))
                continue;
            inAnyOutfit = true;
            if (!IsPieceInMirageOutfitSlot(row, itemId))
                return false;
        }

        return inAnyOutfit;
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

    private static bool IsPieceInOutfitDefinition(MirageStoreSetItem row, uint pieceItemId) {
        foreach (var itemRef in row.Items) {
            if (itemRef.RowId != 0 && itemRef.RowId == pieceItemId)
                return true;
        }

        return false;
    }

    private static bool IsPieceInMirageOutfitSlot(MirageStoreSetItem row, uint pieceItemId) {
        var slotIndex = 0;
        foreach (var itemRef in row.Items) {
            if (itemRef.RowId != 0 && itemRef.RowId == pieceItemId && row.IsSetSlotCollected(slotIndex))
                return true;
            slotIndex++;
        }

        return false;
    }

    private static bool IsPieceInMirageOutfitSlot(uint pieceItemId)
        => MirageStoreSetItem.Where(r => r.RowId > 0).Any(r => IsPieceInMirageOutfitSlot(r, pieceItemId));

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
