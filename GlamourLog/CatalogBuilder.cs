using FFXIVClientStructs.FFXIV.Client.Game;

namespace GlamourLog;

internal readonly record struct CatalogBuildResult(Catalog Catalog, ReadOnlyCollection<GlamourSet> Sets, HashSet<uint> ArmoireItemIds);

/// <summary> Login-time catalog topology, supplemental loot maps, mirage set classification, and armoire id snapshot. </summary>
internal static unsafe class CatalogBuilder {
    internal static uint[] GetTradecraftDiscriminators()
        => [.. new byte[] { 1, 2, 3, 4, 6, 7 }
            .Select(sid => CurrencyManager.Instance()->GetItemIdBySpecialId(sid))
            .Where(id => id != 0)
            .Distinct()];

    internal static HashSet<uint> LoadArmoireItemIds()
        => [.. Cabinet.Where(x => x.RowId > 0 && x.Item.RowId > 0).Select(x => x.Item.RowId)];

    internal static ReadOnlyCollection<GlamourSet> BuildClassifiedSets(Catalog catalog, ItemCostLookup costsLookup, byte pvpSeries) {
        var specialShopByItemId = Catalog.BuildSpecialShopByReceiveItemId();
        var itemByRowId = Item.Where(i => i.RowId > 0).ToDictionary(i => i.RowId);
        return MirageStoreSetItem.Where(x => x.RowId > 0).Select(x => {
            var items = x.Items
                .Where(i => i.RowId > 0)
                .Select(i => i.RowId)
                .ToList()
                .AsReadOnly();
            var specialShopRow = Enumerable.FirstOrDefault(
                Enumerable.Select<uint, SpecialShop?>(items, id => specialShopByItemId.TryGetValue(id, out var s) ? s : null),
                s => s is not null);

            var r = catalog.ClassifySet(x, items, costsLookup, specialShopRow, pvpSeries);

            var name = itemByRowId.TryGetValue(x.RowId, out var itemRow) ? itemRow.Name.ToString() : string.Empty;
            return new GlamourSet { ItemId = x.RowId, Name = name, Items = items, CategoryName = r.CategoryName, IsUnobtainable = r.IsUnobtainable };
        })
        .Where(g => g.Items.Count > 0 && !string.IsNullOrWhiteSpace(g.Name))
        .OrderBy(x => x.Name)
        .ThenBy(x => x.ItemId)
        .ToList()
        .AsReadOnly();
    }

    internal static CatalogBuildResult Run(ItemCostLookup costsLookup) {
        var pvpSeries = FFXIVClientStructs.FFXIV.Client.Game.UI.PvPProfile.Instance()->Series;
        var catalog = Catalog.Build(costsLookup, GetTradecraftDiscriminators());
        var sets = BuildClassifiedSets(catalog, costsLookup, pvpSeries);
        return new CatalogBuildResult(catalog, sets, LoadArmoireItemIds());
    }
}
