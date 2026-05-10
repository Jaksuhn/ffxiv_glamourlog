using FFXIVClientStructs.FFXIV.Client.Game;
using GlamourLog.Services;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

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

    /// <summary> Inclusive range on item ids (matches <see cref="ItemPatch.ToItemLookup"/>); bounds may appear in either order. </summary>
    private static decimal PatchNoForItemId(uint itemId, IReadOnlyList<ItemPatch> itemPatches) {
        foreach (var p in itemPatches) {
            var lo = Math.Min(p.StartItemId, p.EndItemId);
            var hi = Math.Max(p.StartItemId, p.EndItemId);
            if (itemId >= lo && itemId <= hi)
                return p.PatchNo;
        }

        return 0m;
    }

    /// <summary> Mirage set <see cref="GlamourSet.ItemId"/> is the boxed set token; <see cref="ItemPatch"/> ranges use gear ids, so resolve patch from the first piece that matches. </summary>
    private static decimal PatchNoForSetPieces(IReadOnlyList<uint> pieceIds, IReadOnlyList<ItemPatch> itemPatches) {
        foreach (var pieceId in pieceIds) {
            var patch = PatchNoForItemId(pieceId, itemPatches);
            if (patch != 0m)
                return patch;
        }

        return 0m;
    }

    internal static ReadOnlyCollection<GlamourSet> BuildClassifiedSets(Catalog catalog, ItemCostLookup costsLookup, byte pvpSeries, IReadOnlyList<ItemPatch> itemPatches) {
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

            var name = string.Empty;
            uint sortIl = 0;
            if (itemByRowId.TryGetValue(x.RowId, out var itemRow)) {
                name = itemRow.Name.ToString();
                sortIl = itemRow.LevelItem.RowId;
            }

            var sortPatch = PatchNoForSetPieces(items, itemPatches);
            return new GlamourSet {
                ItemId = x.RowId,
                Name = name,
                Items = items,
                CategoryName = r.CategoryName,
                IsUnobtainable = r.IsUnobtainable,
                SortItemLevel = sortIl,
                SortPatchNo = sortPatch,
            };
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
        var itemPatches = LoadItemPatches();
        var sets = BuildClassifiedSets(catalog, costsLookup, pvpSeries, itemPatches);
        return new CatalogBuildResult(catalog, sets, LoadArmoireItemIds());
    }

    private static IReadOnlyList<ItemPatch> LoadItemPatches() {
        var list = (IReadOnlyList<ItemPatch>)[.. Svc.Data.GetSupplemental<ItemPatch>(CsvLoader.ItemPatchResourceName)];
        if (list.Count == 0)
            Svc.Log.Warning($"[{nameof(CatalogBuilder)}] No ItemPatch supplemental rows (resource \"{CsvLoader.ItemPatchResourceName}\"). Patch sort falls back to name order.");
        return list;
    }
}
