using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Collections.Frozen;
using System.Globalization;

namespace GlamourLog.Services;

/// <summary>Consistent ownership view for one capture. Prefer over repeated one-shot service calls when painting UI.</summary>
internal sealed class OwnershipQuery {
    private readonly Snapshot _snap;
    private readonly Dictionary<GlamourSet, SetStatus> _cache = [];

    private OwnershipQuery(Snapshot snap) => _snap = snap;

    internal static OwnershipQuery Capture(OwnershipService ownership) {
        var catalog = Svc.Get<CatalogService>();
        var dresser = ownership.GetDresserItemIds();
        var armoire = ownership.GetArmoireItemIds();
        var setTokens = catalog.GlamourSets.Where(s => !s.NonSetCabinetPiece).Select(s => s.ItemId).ToHashSet();
        HashSet<uint> storage = [.. dresser.Where(id => !setTokens.Contains(id))];
        storage.UnionWith(armoire);
        var inventory = InventoryType.AllPlayer.SelectMany(inv => inv.Items.Where(i => i.ItemId != 0)).Select(item => item.BaseItemId).ToHashSet();
        return new OwnershipQuery(new Snapshot {
            DresserItemIds = dresser,
            ArmoireOwnedItemIds = armoire,
            StorageOwnedItems = storage,
            InventoryItemIds = inventory,
            ArmoireCatalogItemIds = catalog.ArmoireItemIds,
        });
    }

    internal SetStatus For(GlamourSet set) {
        if (_cache.TryGetValue(set, out var cached))
            return cached;
        var status = Resolve(set);
        _cache[set] = status;
        return status;
    }

    internal int CountCompleteIn(IEnumerable<GlamourSet> sets)
        => sets.Count(s => For(s).IsComplete);

    /// <summary>Set-scoped when <paramref name="set"/> is provided; otherwise any outfit counts as OutfitSlot.</summary>
    internal PieceLocation Locate(uint itemId, GlamourSet? set = null) {
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        if (itemId == 0)
            return PieceLocation.None;
        if (_snap.ArmoireOwnedItemIds.Contains(itemId) || OwnershipService.IsInCabinet(itemId))
            return PieceLocation.Armoire;
        if (set is { NonSetCabinetPiece: false, ItemId: not 0 } && _snap.DresserItemIds.Contains(set.ItemId) && OwnershipService.IsPieceInMirageOutfitSlot(MirageStoreSetItem.GetRow(set.ItemId), itemId))
            return PieceLocation.OutfitSlot;
        if (set is null && OwnershipService.IsPieceInAnyMirageOutfitSlot(itemId))
            return PieceLocation.OutfitSlot;
        if (_snap.StorageOwnedItems.Contains(itemId) && !_snap.ArmoireOwnedItemIds.Contains(itemId))
            return PieceLocation.LooseDresser;
        if (_snap.InventoryItemIds.Contains(itemId))
            return PieceLocation.Inventory;
        return PieceLocation.None;
    }

    internal OwnershipQuery WithOwnedItemOverride(uint itemId, bool owned) {
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        var inventory = new HashSet<uint>(_snap.InventoryItemIds);
        if (owned)
            inventory.Add(itemId);
        else
            inventory.Remove(itemId);
        return new OwnershipQuery(_snap with { InventoryItemIds = inventory });
    }

    internal bool IsDresserListed(uint itemId) => _snap.DresserItemIds.Contains(itemId);

    private SetStatus Resolve(GlamourSet set) {
        var pieces = new List<PieceStatus>(set.Items.Count);
        foreach (var rawId in set.Items) {
            var itemId = ItemUtil.GetBaseId(rawId).ItemId;
            if (itemId == 0)
                continue;
            var location = Locate(itemId, set);
            var display = location.ToStorageState();
            pieces.Add(new PieceStatus {
                ItemId = itemId,
                Location = location,
                DisplayStorage = display,
                ShowArmoireWarning = display is ItemStorageState.DresserSet or ItemStorageState.DresserLoose && _snap.ArmoireCatalogItemIds.Contains(itemId),
            });
        }

        var ownedCount = pieces.Count(p => p.IsOwned);
        var isComplete = ComputeIsComplete(set, pieces);
        var storage = ComputeSetStorage(isComplete, pieces);
        return new SetStatus {
            Set = set,
            Pieces = pieces,
            IsComplete = isComplete,
            OwnedCount = ownedCount,
            Storage = storage,
            ArmoireMisplaced = storage is SetStorageState.Dresser && set.Items.Any(_snap.ArmoireCatalogItemIds.Contains) || pieces.Any(p => p.ShowArmoireWarning),
            HasContributableInventoryPiece = pieces.Any(p => p.Location is PieceLocation.Inventory),
            CanAffordMissing = ComputeCanAffordMissing(set),
        };
    }

    private bool ComputeIsComplete(GlamourSet set, List<PieceStatus> pieces) {
        if (set.NonSetCabinetPiece)
            return pieces.Count > 0 && pieces.All(p => p.IsStored);
        if (_snap.DresserItemIds.Contains(set.ItemId) && OwnershipService.IsFullMirageOutfit(set))
            return true;
        return pieces.Count == set.Items.Count && pieces.All(p => p.IsStored);
    }

    private static SetStorageState ComputeSetStorage(bool isComplete, List<PieceStatus> pieces) {
        if (!isComplete)
            return SetStorageState.None;
        var states = pieces.Select(p => p.DisplayStorage).ToHashSet();
        if (states.Contains(ItemStorageState.DresserLoose))
            return SetStorageState.None;
        if (states.Contains(ItemStorageState.Armoire) && states.Contains(ItemStorageState.DresserSet))
            return SetStorageState.Mixed;
        if (states.Contains(ItemStorageState.Armoire))
            return SetStorageState.Armoire;
        if (states.Contains(ItemStorageState.DresserSet))
            return SetStorageState.Dresser;
        return SetStorageState.None;
    }

    private bool ComputeCanAffordMissing(GlamourSet set) {
        var catalog = Svc.Get<CatalogService>();
        var category = catalog.CategoryNameForPrimaryCostLookup(set);
        var totals = new Dictionary<uint, uint>();
        foreach (var itemId in set.Items) {
            if (_snap.StorageOwnedItems.Contains(itemId) || _snap.InventoryItemIds.Contains(itemId))
                continue;
            // Outfit-deposited pieces also count as owned for affordability
            if (Locate(itemId, set) is not PieceLocation.None)
                continue;
            var costs = catalog.GetPrimaryItemCosts(itemId, category);
            if (costs.Count == 0)
                return false;
            foreach (var (costItemId, amount) in costs) {
                totals.TryGetValue(costItemId, out var total);
                totals[costItemId] = total + amount;
            }
        }

        if (totals.Count == 0)
            return true;

        foreach (var (costItemId, totalAmount) in totals) {
            if (totalAmount > OwnershipService.GetOwnedCurrencyCount(costItemId))
                return false;
        }
        return true;
    }

    private readonly struct Snapshot {
        internal HashSet<uint> DresserItemIds { get; init; }
        internal HashSet<uint> ArmoireOwnedItemIds { get; init; }
        internal HashSet<uint> StorageOwnedItems { get; init; }
        internal HashSet<uint> InventoryItemIds { get; init; }
        internal HashSet<uint> ArmoireCatalogItemIds { get; init; }
    }
}

internal static class PieceLocationExtensions {
    internal static ItemStorageState ToStorageState(this PieceLocation location) => location switch {
        PieceLocation.Armoire => ItemStorageState.Armoire,
        PieceLocation.OutfitSlot => ItemStorageState.DresserSet,
        PieceLocation.LooseDresser => ItemStorageState.DresserLoose,
        _ => ItemStorageState.None,
    };
}

internal sealed unsafe class OwnershipService : IDisposable {
    private static readonly Lazy<FrozenDictionary<uint, uint>> CabinetLookup = new(()
        => Cabinet.Where(row => row.Item.RowId != 0)
            .ToFrozenDictionary(row => row.Item.RowId, row => row.RowId));
    private static readonly Lazy<FrozenDictionary<uint, uint>> CabinetByRowLookup = new(()
        => Cabinet.Where(row => row.RowId > 0 && row.Item.RowId != 0)
            .ToFrozenDictionary(row => row.RowId, row => row.Item.RowId));

    public OwnershipService() {
        Svc.Armoire.ArmoireChanged += OnArmoireChanged;
        Svc.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose() {
        Svc.GameInventory.InventoryChanged -= OnInventoryChanged;
        Svc.Armoire.ArmoireChanged -= OnArmoireChanged;
    }

    internal event System.Action? ArmoireOwnershipChanged;

    private void OnArmoireChanged() {
        var catalog = Svc.Get<CatalogService>();
        catalog.OnArmoireChanged();
        catalog.NotifyOwnershipChanged();
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

    internal OwnershipQuery Query() => OwnershipQuery.Capture(this);

    internal bool IsSetComplete(uint setItemId) {
        var set = Svc.Get<CatalogService>().GlamourSets.FirstOrDefault(s => s.ItemId == setItemId);
        return set is not null && Query().For(set).IsComplete;
    }

    internal bool IsItemInDresser(uint itemId) {
        var q = Query();
        var catalog = Svc.Get<CatalogService>();
        if (q.IsDresserListed(itemId) && catalog.GlamourSets.All(s => s.ItemId != itemId))
            return true;
        return catalog.GlamourSets.Any(s => s.Items.Contains(itemId) && q.Locate(itemId, s) is PieceLocation.OutfitSlot);
    }

    internal HashSet<uint> GetDresserItemIds() {
        var finder = ItemFinderModule.Instance();
        return finder is null ? [] : [.. finder->GlamourDresserBaseItemIds];
    }

    internal HashSet<uint> GetArmoireItemIds() {
        var owned = new HashSet<uint>();
        foreach (var itemId in Svc.Armoire.GetArmoireItems())
            owned.Add(ItemUtil.GetBaseId(itemId).ItemId);
        foreach (var itemId in CabinetLookup.Value.Keys) {
            if (IsInCabinet(itemId))
                owned.Add(itemId);
        }
        return owned;
    }

    internal void GetLalaAchievementsExportBuckets(out Dictionary<string, uint[]> outfitsBySetId, out uint[] armoires) {
        var dresserIds = GetDresserItemIds();
        var outfitsBuilder = new Dictionary<string, HashSet<uint>>();
        foreach (var set in Svc.Get<CatalogService>().GlamourSets) {
            if (set.ItemId == 0 || !dresserIds.Contains(set.ItemId))
                continue;
            var setRow = MirageStoreSetItem.GetRow(set.ItemId);
            var pieces = new HashSet<uint>();
            foreach (var pieceId in set.Items) {
                if (pieceId != 0 && IsPieceInMirageOutfitSlot(setRow, pieceId))
                    pieces.Add(pieceId);
            }
            if (pieces.Count > 0)
                outfitsBuilder[set.ItemId.ToString()] = pieces;
        }

        outfitsBySetId = [];
        foreach (var key in outfitsBuilder.Keys.OrderBy(k => uint.Parse(k, CultureInfo.InvariantCulture)))
            outfitsBySetId[key] = [.. outfitsBuilder[key].OrderBy(x => x)];
        armoires = [.. GetArmoireItemIds().Where(id => id != 0).OrderBy(x => x)];
    }

    internal bool IsCrystallizeItemFullyDeposited(uint itemId) {
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        if (itemId == 0)
            return false;
        var dresser = GetDresserItemIds();
        var setTokens = Svc.Get<CatalogService>().GlamourSets.Where(s => !s.NonSetCabinetPiece).Select(s => s.ItemId).ToHashSet();
        if (dresser.Contains(itemId) && !setTokens.Contains(itemId))
            return true;

        var inAnyOutfit = false;
        foreach (var row in MirageStoreSetItem.Where(r => r.RowId > 0)) {
            if (!row.Items.Any(itemRef => itemRef.RowId != 0 && itemRef.RowId == itemId))
                continue;
            inAnyOutfit = true;
            if (!IsPieceInMirageOutfitSlot(row, itemId))
                return false;
        }
        return inAnyOutfit;
    }

    internal static int GetOwnedCurrencyCount(uint costItemId) {
        if (costItemId is not 1 && Svc.Get<AllaganToolsIpc>().TryGetOwnedCount(costItemId, out var allaganCount))
            return allaganCount;
        return CurrencyManager.Instance()->SpecialItemBucket.TryGetValue(costItemId, out var value, true) ? (int)value.Count : InventoryManager.Instance()->GetInventoryItemCount(costItemId);
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

    internal bool IsCabinetItem(uint itemId)
        => ItemUtil.GetBaseId(itemId).ItemId is var baseId and not 0 && CabinetLookup.Value.ContainsKey(baseId);

    internal bool IsItemInArmoire(uint itemId)
        => GetItemIdFromLookups(itemId) is var id and not 0
            && (Svc.Armoire.GetArmoireItems().Any(rawId => ItemUtil.GetBaseId(rawId).ItemId == id) || IsInCabinet(id));

    internal static bool IsInCabinet(uint itemId) {
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

    internal static bool IsFullMirageOutfit(GlamourSet set) {
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

    internal static bool IsPieceInMirageOutfitSlot(MirageStoreSetItem row, uint pieceItemId)
        => row.Items.Select((itemRef, slotIndex) => (itemRef, slotIndex))
            .Any(x => x.itemRef.RowId != 0 && x.itemRef.RowId == pieceItemId && row.IsSetSlotCollected(x.slotIndex));

    internal static bool IsPieceInAnyMirageOutfitSlot(uint pieceItemId)
        => MirageStoreSetItem.Where(r => r.RowId > 0).Any(r => IsPieceInMirageOutfitSlot(r, pieceItemId));
}
