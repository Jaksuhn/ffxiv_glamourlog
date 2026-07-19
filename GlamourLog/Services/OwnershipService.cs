using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Globalization;

namespace GlamourLog.Services;

// frozen ownership snapshot for one ui paint - this gets reused instead of calling services repeatedly mid-frame
internal sealed class OwnershipQuery {
    private readonly Snapshot _snap;
    private readonly Dictionary<GlamourSet, SetStatus> _cache = [];

    private OwnershipQuery(Snapshot snap) => _snap = snap;

    internal static OwnershipQuery Capture(OwnershipService ownership) {
        var catalog = Svc.Get<CatalogService>();
        var dresser = ownership.GetDresserItemIds();
        var armoire = ownership.GetArmoireItemIds();
        var setTokens = catalog.GlamourSets.Where(s => !s.NonSetCabinetPiece).Select(s => s.ItemId).ToHashSet();
        HashSet<uint> storage = [.. dresser.Where(id => !setTokens.Contains(id))]; // set tokens aren't "owned pieces" — only loose dresser + armoire items belong here
        storage.UnionWith(armoire);
        var inventory = Svc.Items.GetInventoryItemIds();
        return new OwnershipQuery(new Snapshot {
            DresserItemIds = dresser,
            ArmoireOwnedItemIds = armoire,
            StoredItemIds = storage,
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

    // with a set: only counts as outfit-slot if it's in that set's outfit. without: any mirage outfit counts
    internal PieceLocation Locate(uint itemId, GlamourSet? set = null) {
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        if (itemId == 0)
            return PieceLocation.None;
        if (_snap.ArmoireOwnedItemIds.Contains(itemId) || Svc.Items.IsInCabinet(itemId)) // cache can lag so immediate check here
            return PieceLocation.Armoire;
        if (set is { NonSetCabinetPiece: false, ItemId: not 0 } && _snap.DresserItemIds.Contains(set.ItemId) && Svc.Items.IsPieceInMirageOutfitSlot(MirageStoreSetItem.GetRow(set.ItemId), itemId))
            return PieceLocation.OutfitSlot;
        if (set is null && Svc.Items.IsPieceInAnyMirageOutfitSlot(itemId))
            return PieceLocation.OutfitSlot;
        if (_snap.StoredItemIds.Contains(itemId) && !_snap.ArmoireOwnedItemIds.Contains(itemId))
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
                BadgeLocation = display,
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
        // completed saved outfit counts no matter what
        if (_snap.DresserItemIds.Contains(set.ItemId) && IsFullMirageOutfit(set))
            return true;
        return pieces.Count == set.Items.Count && pieces.All(p => p.IsStored);
    }

    private static SetStorageState ComputeSetStorage(bool isComplete, List<PieceStatus> pieces) {
        if (!isComplete)
            return SetStorageState.None;
        var states = pieces.Select(p => p.BadgeLocation).ToHashSet();
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
        var category = catalog.GetCategoryForPreferredCost(set);
        var totals = new Dictionary<uint, uint>();
        foreach (var itemId in set.Items) {
            if (_snap.StoredItemIds.Contains(itemId) || _snap.InventoryItemIds.Contains(itemId))
                continue;
            if (Locate(itemId, set) is not PieceLocation.None)
                continue;
            var costs = catalog.GetPrimaryItemCosts(itemId, category);
            if (costs.Count == 0)
                return false; // exclude things with no costs from being affordable
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

    private readonly struct Snapshot {
        internal HashSet<uint> DresserItemIds { get; init; } // includes mirage set tokens + loose plates
        internal HashSet<uint> ArmoireOwnedItemIds { get; init; }
        internal HashSet<uint> StoredItemIds { get; init; } // loose dresser + armoire (no set tokens)
        internal HashSet<uint> InventoryItemIds { get; init; }
        internal HashSet<uint> ArmoireCatalogItemIds { get; init; } // items that *can* go in the armoire
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
    public OwnershipService() {
        Svc.Items.ArmoireChanged += OnArmoireChanged;
        Svc.Items.DresserChanged += OnDresserChanged;
        Svc.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose() {
        Svc.GameInventory.InventoryChanged -= OnInventoryChanged;
        Svc.Items.DresserChanged -= OnDresserChanged;
        Svc.Items.ArmoireChanged -= OnArmoireChanged;
    }

    internal event System.Action? ArmoireOwnershipChanged;

    private void OnArmoireChanged() {
        var catalog = Svc.Get<CatalogService>();
        catalog.OnArmoireChanged();
        catalog.NotifyOwnershipChanged();
        ArmoireOwnershipChanged?.Invoke();
    }

    private void OnDresserChanged() {
        Svc.Get<CatalogService>().NotifyOwnershipChanged();
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

    internal HashSet<uint> GetDresserItemIds() => Svc.Items.GetDresserItemIds();

    internal HashSet<uint> GetArmoireItemIds() => Svc.Items.GetArmoireItemIds();

    // for lala achievements export: outfit pieces keyed by set id, plus deposited armoire items
    internal void BuildLalaExport(out Dictionary<string, uint[]> outfitsBySetId, out uint[] armoires) {
        var dresserIds = GetDresserItemIds();
        var outfitsBuilder = new Dictionary<string, HashSet<uint>>();
        foreach (var set in Svc.Get<CatalogService>().GlamourSets) {
            if (set.ItemId == 0 || !dresserIds.Contains(set.ItemId))
                continue;
            var setRow = MirageStoreSetItem.GetRow(set.ItemId);
            var pieces = new HashSet<uint>();
            foreach (var pieceId in set.Items) {
                if (pieceId != 0 && Svc.Items.IsPieceInMirageOutfitSlot(setRow, pieceId))
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
        var catalog = Svc.Get<CatalogService>();
        var setTokens = catalog.GlamourSets.Where(s => !s.NonSetCabinetPiece).Select(s => s.ItemId).ToHashSet();
        return Svc.Items.IsFullyDepositedInDresser(itemId, setTokens);
    }

    // prefers Allagan Tools for non-gil currencies when available
    // doesn't work for gil because AT stores it as a uint and that can overflow
    internal static int GetOwnedCurrencyCount(uint costItemId) {
        if (costItemId is not 1 && Svc.Get<AllaganToolsIpc>().TryGetOwnedCount(costItemId, out var allaganCount))
            return allaganCount;
        return Svc.Items.GetOwnedCurrencyCount(costItemId);
    }

    internal uint GetItemIdFromLookups(uint cacheOrEntryId)
        => Svc.Items.ResolveCabinetItemId(cacheOrEntryId);

    internal bool IsCabinetItem(uint itemId)
        => Svc.Items.IsCabinetItem(itemId);

    internal bool IsItemInArmoire(uint itemId)
        => Svc.Items.IsInArmoire(itemId);
}
