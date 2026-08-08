using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Globalization;

namespace GlamourLog.Services;

// snapshot used in one ui paint - this gets reused instead of calling services repeatedly mid-frame
internal sealed class OwnershipQuery {
    private readonly Snapshot _snap;
    private readonly Dictionary<GlamourSet, SetStatus> _cache = [];

    private OwnershipQuery(Snapshot snap) => _snap = snap;

    internal static OwnershipQuery Capture(OwnershipService ownership) {
        var catalog = CatalogService.Get();
        var dresser = ownership.GetDresserItemIds();
        var armoire = ownership.GetArmoireItemIds();
        var setTokens = catalog.MirageSetTokenIds;
        HashSet<uint> storage = [.. dresser.Where(id => !setTokens.Contains(id))]; // set tokens aren't "owned pieces", only loose dresser + armoire items belong here
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

    internal CompletionCounts CountCompletions(IEnumerable<GlamourSet> sets) {
        var ownedObtainable = 0;
        var totalObtainable = 0;
        var ownedUnobtainable = 0;
        foreach (var set in sets) {
            var complete = For(set).IsComplete;
            if (set.IsUnobtainable) {
                if (complete)
                    ownedUnobtainable++;
                continue;
            }
            totalObtainable++;
            if (complete)
                ownedObtainable++;
        }
        return new CompletionCounts(ownedObtainable, totalObtainable, ownedUnobtainable);
    }

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
            CanAffordMissing = C.HideUnaffordable && ComputeCanAffordMissing(set),
        };
    }

    private bool ComputeIsComplete(GlamourSet set, List<PieceStatus> pieces) {
        if (set.NonSetCabinetPiece)
            return pieces.Count > 0 && pieces.All(p => p.IsStored);
        if (pieces.Count == set.Items.Count && pieces.All(p => p.IsStored)) // already resolved via Locate
            return true;
        return _snap.DresserItemIds.Contains(set.ItemId) && IsFullMirageOutfit(set);
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
        var catalog = CatalogService.Get();
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

internal sealed unsafe class OwnershipService : IPluginService, IDisposable {
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
        var catalog = CatalogService.Get();
        catalog.OnArmoireChanged();
        catalog.NotifyOwnershipChanged();
        ArmoireOwnershipChanged?.Invoke();
    }

    private void OnDresserChanged() {
        CatalogService.Get().NotifyOwnershipChanged();
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
            var catalog = CatalogService.Get();
            if (catalog.GlamourSets.Any(set => set.Items.Contains(itemId)) || catalog.IsKnownCostCurrency(itemId)) {
                catalog.NotifyOwnershipChanged();
                return;
            }
        }
    }

    internal OwnershipQuery Query() => OwnershipQuery.Capture(this);

    internal bool IsSetComplete(uint setItemId) {
        var set = CatalogService.Get().GlamourSets.FirstOrDefault(s => s.ItemId == setItemId);
        return set is not null && Query().For(set).IsComplete;
    }

    internal bool IsContentComplete(uint cfcId) {
        if (cfcId == 0 || ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true })
            return false;
        var catalog = CatalogService.Get();
        if (!catalog.CatalogReady)
            return false;

        var q = Query();
        var acquisition = ItemAcquisitionService.Get();
        var any = false;
        foreach (var set in catalog.GlamourSets) {
            var status = q.For(set);
            foreach (var pieceId in set.Items) {
                if (pieceId == 0)
                    continue;
                if (!acquisition.GetSources(pieceId).Any(src => ItemAcquisitionService.IsSourceFromCfc(src, cfcId)))
                    continue;
                any = true;
                if (status.Piece(pieceId) is not { IsOwned: true })
                    return false;
            }
        }
        return any;
    }

    internal bool IsItemInDresser(uint itemId) {
        var q = Query();
        var catalog = CatalogService.Get();
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
        foreach (var set in CatalogService.Get().GlamourSets) {
            if (set.NonSetCabinetPiece || set.ItemId == 0 || !dresserIds.Contains(set.ItemId))
                continue;
            if (!MirageStoreSetItem.TryGetRow(set.ItemId, out var setRow))
                continue;
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
        var catalog = CatalogService.Get();
        var setTokens = catalog.GlamourSets.Where(s => !s.NonSetCabinetPiece).Select(s => s.ItemId).ToHashSet();
        return Svc.Items.IsFullyDepositedInDresser(itemId, setTokens);
    }

    // prefers Allagan Tools for non-gil currencies when available
    // doesn't work for gil because AT stores it as a uint and that can overflow
    internal static int GetOwnedCurrencyCount(uint costItemId) {
        if (costItemId is not 1 && AllaganToolsIpc.Get().TryGetOwnedCount(costItemId, out var allaganCount))
            return allaganCount;
        return Svc.Items.GetOwnedCurrencyCount(costItemId);
    }

    internal uint GetItemIdFromLookups(uint cacheOrEntryId)
        => Svc.Items.ResolveCabinetItemId(cacheOrEntryId);

    internal bool IsCabinetItem(uint itemId)
        => Svc.Items.IsCabinetItem(itemId);

    internal bool IsItemInArmoire(uint itemId)
        => Svc.Items.IsInArmoire(itemId);

    // true if store-all armoire or dresser would store this item
    internal bool IsItemStorable(uint itemId)
        => IsArmoireStorable(itemId) || IsDresserStorable(itemId);

    private static bool IsArmoireStorable(uint itemId) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0 || !Svc.Items.IsCabinetItem(baseId) || Svc.Items.IsInCabinet(baseId))
            return false;

        var handle = (ItemHandle)baseId;
        return handle.TrySetItemLocation() && !handle.InGearset && !handle.IsRepairable;
    }

    private bool IsDresserStorable(uint itemId) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0 || IsCabinetItem(baseId))
            return false;

        HashSet<uint>? looseDresser = null;
        foreach (var set in CatalogService.Get().GlamourSets) {
            if (set.NonSetCabinetPiece || !set.Items.Any(id => ItemUtil.GetBaseId(id).ItemId == baseId))
                continue;
            if (!MirageStoreSetItem.TryGetRow(set.ItemId, out var setRow))
                continue;

            looseDresser ??= BuildLooseDresserIdSet();
            if (IsDresserPieceStorable(itemId, setRow, CollectOutfitIndices(setRow.RowId), looseDresser))
                return true;
        }

        return false;
    }

    internal bool IsDresserPieceStorable(uint itemId, MirageStoreSetItem setRow, IReadOnlyList<uint> outfitIndices, HashSet<uint> looseDresser) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0 || IsCabinetItem(baseId) || looseDresser.Contains(baseId) || !HasUnsetSlotForPiece(setRow, baseId, outfitIndices))
            return false;

        var handle = (ItemHandle)itemId;
        return handle.TrySetItemLocation() && !handle.InGearset && !handle.IsRepairable;
    }

    internal static unsafe HashSet<uint> BuildLooseDresserIdSet() {
        var finder = ItemFinderModule.Instance();
        if (finder is null)
            return [];

        var ids = new HashSet<uint>();
        foreach (var id in finder->GlamourDresserBaseItemIds) {
            if (id != 0)
                ids.Add(id);
        }

        return ids;
    }

    internal static unsafe List<uint> CollectOutfitIndices(uint setItemId) {
        var mirage = MirageManager.Instance();
        if (mirage is null)
            return [];

        var ids = mirage->PrismBoxItemIds;
        var result = new List<uint>(1);
        for (var i = 0; i < ids.Length; i++) {
            if (ids[i] == setItemId)
                result.Add((uint)i);
        }

        return result;
    }

    private static unsafe bool HasUnsetSlotForPiece(MirageStoreSetItem setRow, uint pieceBaseId, IReadOnlyList<uint> outfitIndices) {
        var mirage = MirageManager.Instance();
        if (mirage is null)
            return true;

        int? pieceSheetSlot = null;
        foreach (var (slotIndex, itemRef) in setRow.Items.Index()) {
            if (itemRef.RowId != 0 && ItemUtil.GetBaseId(itemRef.RowId).ItemId == pieceBaseId) {
                pieceSheetSlot = slotIndex;
                break;
            }
        }

        if (pieceSheetSlot is null)
            return false;
        if (outfitIndices.Count == 0)
            return true;

        foreach (var outfitIndex in outfitIndices) {
            if (!mirage->IsSetSlotUnlocked(outfitIndex, pieceSheetSlot.Value))
                return true;
        }

        return false;
    }
}
