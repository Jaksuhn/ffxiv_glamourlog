using AllaganLib.GameSheets.Sheets;

namespace GlamourLog;

internal readonly record struct CatalogBuildResult(Catalog Catalog, ReadOnlyCollection<GlamourSet> Sets, HashSet<uint> ArmoireItemIds);

internal static class CatalogBuilder {
    internal static HashSet<uint> LoadArmoireItemIds()
        => [.. Cabinet.Where(x => x.RowId > 0 && x.Item.RowId > 0).Select(x => x.Item.RowId)];

    internal static ReadOnlyCollection<GlamourSet> BuildClassifiedSets(Catalog catalog, ItemCostLookup costsLookup, byte pvpSeries) {
        var itemSheet = Svc.SheetManager.GetSheet<ItemSheet>();
        var specialShopByItemId = Catalog.BuildSpecialShopByReceiveItemId();
        var itemByRowId = Item.Where(i => i.RowId > 0).ToDictionary(i => i.RowId);
        return MirageStoreSetItem.Where(x => x.RowId > 0).Select(x => {
            var items = x.Items.Where(i => i.RowId > 0).Select(i => i.RowId).ToList().AsReadOnly();
            var specialShopRow = Enumerable.FirstOrDefault(Enumerable.Select<uint, SpecialShop?>(items, id => specialShopByItemId.TryGetValue(id, out var s) ? s : null), s => s is not null);
            var r = catalog.ClassifySet(x, items, costsLookup, specialShopRow, pvpSeries);

            var name = string.Empty;
            uint sortIl = 0;
            if (itemByRowId.TryGetValue(x.RowId, out var itemRow)) {
                name = itemRow.Name.ToString();
                sortIl = itemRow.LevelItem.RowId;
            }

            // Mirage token row is not in ItemPatch ranges; max patch across gear pieces matches PatchDescending.
            var sortPatch = items.Count == 0 ? 0m : items.Max(id => itemSheet.GetItemPatch(id));
            var modelSignature = SetModelSignature.ForMirageSet(items);
            return new GlamourSet {
                ItemId = x.RowId,
                Name = name,
                Items = items,
                CategoryName = r.CategoryName,
                IsUnobtainable = r.IsUnobtainable,
                SortItemLevel = sortIl,
                SortPatchNo = sortPatch,
                NonSetCabinetPiece = false,
                IsIncompatible = x.Items.None(i => i.Value.EquipRestriction.Value.CanEquip),
                ModelSignature = modelSignature,
                SharedModelGroupSize = 1,
                HasPartialSharedModels = false,
            };
        })
        .Where(g => g.Items.Count > 0 && !string.IsNullOrWhiteSpace(g.Name))
        .OrderBy(x => x.Name)
        .ThenBy(x => x.ItemId)
        .ToList()
        .AsReadOnly();
    }

    internal static List<GlamourSet> BuildMiscArmoireEntries(Catalog catalog, HashSet<uint> armoireItemIds, IReadOnlyCollection<GlamourSet> mirageSets) {
        var setPieceIds = mirageSets.SelectMany(s => s.Items).ToHashSet();
        var itemSheet = Svc.SheetManager.GetSheet<ItemSheet>();
        var bucketName = catalog.MiscArmoireBucket.Name;
        var entries = new List<GlamourSet>();

        foreach (var itemId in armoireItemIds.Where(id => !setPieceIds.Contains(id)).OrderBy(id => id)) {
            if (!Item.GetRowRef(itemId).IsValid)
                continue;

            var row = Item.GetRow(itemId);
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var items = new ReadOnlyCollection<uint>([itemId]);
            entries.Add(new GlamourSet {
                ItemId = itemId,
                Name = name,
                Items = items,
                CategoryName = bucketName,
                IsUnobtainable = false,
                SortItemLevel = row.LevelItem.RowId,
                SortPatchNo = itemSheet.GetItemPatch(itemId),
                NonSetCabinetPiece = true,
                IsIncompatible = !row.EquipRestriction.Value.CanEquip,
                ModelSignature = SetModelSignature.ForMiscSingle(itemId),
                SharedModelGroupSize = 1,
                HasPartialSharedModels = false,
            });
        }

        return entries;
    }

    internal static CatalogBuildResult Run(ItemCostLookup costsLookup, byte pvpSeries, IReadOnlyList<uint> tradecraftCurrencyItemIds) {
        var catalog = Catalog.Build(costsLookup, tradecraftCurrencyItemIds);
        var armoireItemIds = LoadArmoireItemIds();
        var mirageSets = BuildClassifiedSets(catalog, costsLookup, pvpSeries);
        var miscArmoireEntries = BuildMiscArmoireEntries(catalog, armoireItemIds, mirageSets);
        var allSets = ApplySharedModelMetadata([.. mirageSets, .. miscArmoireEntries]).AsReadOnly();
        return new CatalogBuildResult(catalog, allSets, armoireItemIds);
    }

    internal static Dictionary<ItemModelInfo, List<uint>> BuildSharedModelItemGroups(IEnumerable<uint> itemIds)
        => itemIds
            .Distinct()
            .GroupBy(static itemId => (ItemModelInfo)itemId)
            .ToDictionary(g => g.Key, g => g.OrderBy(id => id).ToList());

    internal static bool PieceHasSharedModelSiblings(uint itemId, Dictionary<ItemModelInfo, List<uint>> itemGroups) {
        ItemModelInfo model = itemId;
        if (!itemGroups.TryGetValue(model, out var group) || group.Count <= 1)
            return false;
        var slot = Item.GetRow(itemId).EquipSlot;
        return group.Exists(id => id != itemId && Item.GetRow(id).EquipSlot == slot);
    }

    private static List<GlamourSet> ApplySharedModelMetadata(List<GlamourSet> sets) {
        var groupSizes = sets.GroupBy(s => s.ModelSignature).ToDictionary(g => g.Key, g => g.Count());
        var itemGroups = BuildSharedModelItemGroups(sets.SelectMany(s => s.Items));
        return [.. sets.Select(s => {
            var sharedModelGroupSize = groupSizes[s.ModelSignature];
            var piecesWithSiblings = s.Items.Count(id => PieceHasSharedModelSiblings(id, itemGroups));
            var hasPartialSharedModels = sharedModelGroupSize <= 1
                && piecesWithSiblings > 0
                && piecesWithSiblings < s.Items.Count;
            return new GlamourSet {
                ItemId = s.ItemId,
                Name = s.Name,
                Items = s.Items,
                CategoryName = s.CategoryName,
                IsUnobtainable = s.IsUnobtainable,
                SortItemLevel = s.SortItemLevel,
                SortPatchNo = s.SortPatchNo,
                NonSetCabinetPiece = s.NonSetCabinetPiece,
                IsIncompatible = s.IsIncompatible,
                ModelSignature = s.ModelSignature,
                SharedModelGroupSize = sharedModelGroupSize,
                HasPartialSharedModels = hasPartialSharedModels,
            };
        })];
    }
}
