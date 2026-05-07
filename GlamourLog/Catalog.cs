using Dalamud.Game;
using GlamourLog.Services;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;
using System.Collections.Immutable;

namespace GlamourLog;

internal sealed class Catalog {
    public static readonly ImmutableHashSet<uint> UnobtainableMirageRowIds = new HashSet<uint> { 45320, 45248, 45247, 45306, 45340, 45289, 45339, 45222, 45330, 45223, 45424, 45423 }.ToImmutableHashSet();

    public IReadOnlyList<OutfitCategory> UITabsInOrder { get; }
    public IReadOnlyList<OutfitCategory> ClassifiableCategories { get; }
    public OutfitCategory PvpSeriesAttire { get; }
    public OutfitCategory DungeonChest { get; }
    public OutfitCategory UncategorizedBucket { get; }
    public OutfitCategory UnobtainableBucket { get; }

    public IReadOnlyDictionary<uint, IReadOnlyList<DungeonChestItemProvenance>> ChestLootProvenanceByItemId { get; }
    public IReadOnlyDictionary<uint, IReadOnlyList<FateItemProvenance>> FateLootProvenanceByItemId { get; }

    private Catalog(
        IReadOnlyList<OutfitCategory> uiTabsInOrder,
        IReadOnlyList<OutfitCategory> classifiableCategories,
        OutfitCategory pvpSeriesAttire,
        OutfitCategory dungeonChest,
        OutfitCategory uncategorizedBucket,
        OutfitCategory unobtainableBucket,
        IReadOnlyDictionary<uint, IReadOnlyList<DungeonChestItemProvenance>> chestLootProvenanceByItemId,
        IReadOnlyDictionary<uint, IReadOnlyList<FateItemProvenance>> fateLootProvenanceByItemId) {
        UITabsInOrder = uiTabsInOrder;
        ClassifiableCategories = classifiableCategories;
        PvpSeriesAttire = pvpSeriesAttire;
        DungeonChest = dungeonChest;
        UncategorizedBucket = uncategorizedBucket;
        UnobtainableBucket = unobtainableBucket;
        ChestLootProvenanceByItemId = chestLootProvenanceByItemId;
        FateLootProvenanceByItemId = fateLootProvenanceByItemId;
    }

    internal static bool IsContentFinderType(ContentFinderCondition row, params uint[] contentTypeRowIds)
        => contentTypeRowIds.Contains(row.ContentType.RowId);

    internal static HashSet<uint> BuildCurrencyIdsFromCfcSupplemental<T>(string resourceName, Func<T, uint> itemIdSelector, Func<T, uint> cfcIdSelector, Func<T, bool>? rowFilter, params uint[] allowedContentTypes) where T : ICsv, new()
        => [.. Svc.Data.GetSupplemental<T>(resourceName)
            .Where(r => (rowFilter?.Invoke(r) ?? true) && itemIdSelector(r) != 0
            && cfcIdSelector(r) is not 0 and var cfcId && ContentFinderCondition.GetRowRef(cfcId) is { IsValid: true } cfc
            && allowedContentTypes.Contains(cfc.Value.ContentType.RowId)).Select(r => itemIdSelector(r))];

    /// <summary> Supplemental chest loot: <see cref="DungeonChestItem.ChestId"/> is the FK to <see cref="DungeonChest.RowId"/> (same value; do not match on <see cref="DungeonChest.ChestId"/>). Classifies under Dungeons; Raids uses <see cref="DungeonBossDrop"/> currencies only.</summary>
    internal static (HashSet<uint> DungeonChestPieceIds, IReadOnlyDictionary<uint, IReadOnlyList<DungeonChestItemProvenance>> Provenance) BuildChestLootFromSupplemental() {
        var chestByRowId = new Dictionary<uint, DungeonChest>();
        foreach (var chest in Svc.Data.GetSupplemental<DungeonChest>(CsvLoader.DungeonChestResourceName))
            chestByRowId[chest.RowId] = chest;

        var dungeonPieces = new HashSet<uint>();
        var provenanceLists = new Dictionary<uint, List<DungeonChestItemProvenance>>();

        foreach (var itemRow in Svc.Data.GetSupplemental<DungeonChestItem>(CsvLoader.DungeonChestItemResourceName)) {
            if (itemRow.ItemId == 0) continue;
            if (!chestByRowId.TryGetValue(itemRow.ChestId, out var chest))
                continue;

            var prov = new DungeonChestItemProvenance(chest.ContentFinderConditionId, chest.ChestNo, chest.TerritoryTypeId);
            if (!provenanceLists.TryGetValue(itemRow.ItemId, out var list)) {
                list = [];
                provenanceLists[itemRow.ItemId] = list;
            }
            list.Add(prov);
            dungeonPieces.Add(itemRow.ItemId);
        }

        IReadOnlyDictionary<uint, IReadOnlyList<DungeonChestItemProvenance>> provReadonly =
            provenanceLists.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DungeonChestItemProvenance>)[.. kv.Value]);

        return (dungeonPieces, provReadonly);
    }

    internal static IReadOnlyDictionary<uint, IReadOnlyList<FateItemProvenance>> BuildFateLootFromSupplemental() {
        var byItem = new Dictionary<uint, List<FateItemProvenance>>();
        foreach (var row in Svc.Data.GetSupplemental<FateItem>(CsvLoader.FateItemResourceName)) {
            if (row.ItemId == 0 || row.FateId == 0)
                continue;
            if (!byItem.TryGetValue(row.ItemId, out var list)) {
                list = [];
                byItem[row.ItemId] = list;
            }
            list.Add(new FateItemProvenance(row.FateId));
        }

        return byItem.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<FateItemProvenance>)[.. kv.Value.DistinctBy(x => x.FateId).OrderBy(x => x.FateId)]);
    }

    private string? ClassifyFromRules(ClassifyContext ctx) {
        foreach (var cat in ClassifiableCategories) {
            foreach (var rule in cat.Rules) {
                if (rule.Phase != 0) continue;
                if (rule.TryMatch(ctx) is { } name)
                    return name;
            }
        }
        foreach (var cat in ClassifiableCategories) {
            foreach (var rule in cat.Rules) {
                if (rule.Phase != 1) continue;
                if (rule.TryMatch(ctx) is { } name)
                    return name;
            }
        }
        foreach (var cat in ClassifiableCategories) {
            foreach (var rule in cat.Rules) {
                if (rule.Phase != 2) continue;
                if (rule.TryMatch(ctx) is { } name)
                    return name;
            }
        }
        return null;
    }

    public ClassifyResult ClassifySet(MirageStoreSetItem mirageRow, ReadOnlyCollection<uint> itemIds, ItemCostLookup costsLookup, SpecialShop? specialShopRow, byte clientPvpSeries) {
        var ctx = new ClassifyContext(mirageRow, itemIds, costsLookup, specialShopRow);
        foreach (var row in Svc.Data.GetSheet<PvPSeries>().Skip(1)) {
            if (!row.AttireItems.ContainsAll(itemIds)) continue;
            if (row.RowId == clientPvpSeries)
                return new ClassifyResult(PvpSeriesAttire.Name, false);
            var hasCosts = itemIds.Any(i => costsLookup.GetItemCosts(i).Count > 0);
            if (!hasCosts)
                return new ClassifyResult(null, true);
            if (ClassifyFromRules(ctx) is { } fromPvp)
                return new ClassifyResult(fromPvp, false);
        }
        if (ClassifyFromRules(ctx) is { } cat)
            return new ClassifyResult(cat, false);
        if (UnobtainableMirageRowIds.Contains(mirageRow.RowId))
            return new ClassifyResult(null, true);
        return new ClassifyResult(null, false);
    }

    public string BucketKey(ClassifyResult r)
        => r.IsUnobtainable ? UnobtainableBucket.Name : (r.CategoryName ?? UncategorizedBucket.Name);

    public bool IncludeAfterArmoireFilter(IReadOnlyList<uint> items, ItemCostLookup costsLookup, HashSet<uint> armoireItems) {
        if (items.Count == 0)
            return false;
        if (items.Any(id => !armoireItems.Contains(id)))
            return true;
        var chest = DungeonChest.Discriminator.PieceOrCostItemIds;
        if (chest is null || chest.Count == 0)
            return false;
        return items.Any(id => costsLookup.GetItemCosts(id).Any(c => chest.Contains(c.ItemId)));
    }

    /// <summary> Placeholder until <see cref="Build"/> runs after login (Tradecraft needs <see cref="CurrencyManager"/>).</summary>
    internal static Catalog CreateEmptyStub() {
        var uncategorized = new OutfitCategory("Unsorted", int.MinValue) { IsSyntheticBucket = true };
        var unobtainableBucket = new OutfitCategory("Unobtainable", int.MaxValue) { IsSyntheticBucket = true };
        var pvp = new OutfitCategory("PvP", 1);
        var dungeons = new OutfitCategory("Dungeons", 8);
        OutfitCategory[] classifiable = [];
        var uiTabs = new List<OutfitCategory> { uncategorized, unobtainableBucket };
        IReadOnlyDictionary<uint, IReadOnlyList<DungeonChestItemProvenance>> emptyChest = new Dictionary<uint, IReadOnlyList<DungeonChestItemProvenance>>();
        IReadOnlyDictionary<uint, IReadOnlyList<FateItemProvenance>> emptyFate = new Dictionary<uint, IReadOnlyList<FateItemProvenance>>();
        return new Catalog(uiTabs, classifiable, pvp, dungeons, uncategorized, unobtainableBucket, emptyChest, emptyFate);
    }

    /// <summary> Builds catalog topology. <paramref name="tradecraftCurrencyItemIds"/> must be resolved via <see cref="CurrencyManager"/> after login.</summary>
    public static Catalog Build(ItemCostLookup costs, IReadOnlyList<uint> tradecraftCurrencyItemIds) {
        _ = costs;
        var (dungeonChestPieces, chestLootProv) = BuildChestLootFromSupplemental();
        var fateLootProv = BuildFateLootFromSupplemental();

        static OutfitCategory Cat(string name, int uiP) => new(name, uiP);

        var goldSaucer = Cat("Gold Saucer", 0);
        goldSaucer.Discriminator.LateCostCurrencyItemIds.AddRange([29, 41629]);
        goldSaucer.Rules.Add(new LateTabBundleRule(goldSaucer));

        var pvp = Cat("PvP", 1);
        pvp.Discriminator.LateCostCurrencyItemIds.AddRange([25, 36656, 40479]);
        pvp.Rules.Add(new LateTabBundleRule(pvp));

        var tribes = Cat("Tribes", 2);
        tribes.Discriminator.LateCostCurrencyItemIds.AddRange(BeastTribe.Where(r => r.CurrencyItem.RowId != 0).Select(r => r.CurrencyItem.RowId));
        tribes.Rules.Add(new LateTabBundleRule(tribes));

        var gil = Cat("Gil", 3);
        gil.Discriminator.LateCostCurrencyItemIds.Add(1);
        gil.Discriminator.CostAmount = amount => amount > 0;
        gil.Rules.Add(new LateTabBundleRule(gil));

        var tradecraft = Cat("Tradecraft", 4);
        tradecraft.Discriminator.LateCostCurrencyItemIds.AddRange(tradecraftCurrencyItemIds);
        tradecraft.Rules.Add(new LateTabBundleRule(tradecraft));

        var jobGear = Cat("Job Gear", 5);
        jobGear.Discriminator.SpecialShopPredicate = shop => shop.UseCurrencyType == 8 && shop.Quest.RowId > 0;
        jobGear.Rules.Add(new LateTabBundleRule(jobGear));

        var eureka = Cat("Eureka", 6);
        eureka.Discriminator.LateCostCurrencyItemIds.AddRange([21801, 21803]);
        eureka.Rules.Add(new LateTabBundleRule(eureka));

        var occultCrescent = Cat("Occult Crescent", 7);
        occultCrescent.Discriminator.LateCostCurrencyItemIds.AddRange([45043, 45044]);
        occultCrescent.Rules.Add(new LateTabBundleRule(occultCrescent));

        var dungeons = Cat("Dungeons", 8);
        dungeons.Discriminator.PieceOrCostItemIds = dungeonChestPieces;
        dungeons.Rules.Add(new PieceInTabItemSetRule(dungeons));
        dungeons.Rules.Add(new CostCurrencyInTabItemSetRule(dungeons));
        dungeons.Rules.Add(new LateTabBundleRule(dungeons));

        var raids = Cat("Raids", 9);
        raids.Discriminator.LateCostCurrencyItemIds.AddRange(BuildCurrencyIdsFromCfcSupplemental<DungeonBossDrop>(
            CsvLoader.DungeonBossDropResourceName,
            r => r.ItemId,
            r => r.ContentFinderConditionId,
            r => r.FightNo is 0,
            5, 28));
        raids.Discriminator.LateCostCurrencyItemIds.AddRange(BuildCurrencyIdsFromCfcSupplemental<DungeonDrop>(
            "DungeonDrop",
            r => r.ItemId,
            r => r.ContentFinderConditionId,
            rowFilter: null,
            5, 28));
        raids.Discriminator.LateCostCurrencyItemIds.AddRange([22599, 23383, 47100]);
        raids.Rules.Add(new LateTabBundleRule(raids));

        var trials = Cat("Trials", 10);
        trials.Discriminator.LateCostCurrencyItemIds.AddRange(BuildCurrencyIdsFromCfcSupplemental<DungeonBossDrop>(
            CsvLoader.DungeonBossDropResourceName,
            r => r.ItemId,
            r => r.ContentFinderConditionId,
            r => r.FightNo is 0,
            4));
        trials.Discriminator.LateCostCurrencyItemIds.AddRange(BuildCurrencyIdsFromCfcSupplemental<DungeonDrop>(
            "DungeonDrop",
            r => r.ItemId,
            r => r.ContentFinderConditionId,
            rowFilter: null,
            4));
        trials.Rules.Add(new LateTabBundleRule(trials));

        var vcDungeons = Cat("V&C Dungeons", 11);
        vcDungeons.Discriminator.LateCostCurrencyItemIds.AddRange([38533, 39884, 41078, 50434]);
        vcDungeons.Rules.Add(new LateTabBundleRule(vcDungeons));

        var deepDungeons = Cat("Deep Dungeons", 12);
        deepDungeons.Discriminator.LateCostCurrencyItemIds.AddRange([15422, 23164, 46186]);
        deepDungeons.Rules.Add(new LateTabBundleRule(deepDungeons));

        var fates = Cat("Fates", 13);
        fates.Discriminator.LateCostCurrencyItemIds.AddRange([12252, 27972, 36634, 41804]);
        fates.Rules.Add(new LateTabBundleRule(fates));

        var island = Cat("Island Sanctuary", 14);
        island.Discriminator.LateCostCurrencyItemIds.AddRange([37549, 37550]);
        island.Rules.Add(new LateTabBundleRule(island));

        var eternalBonding = Cat("Eternal Bonding", 15);
        eternalBonding.Discriminator.ItemPredicate = item => item.WithLanguage(ClientLanguage.English).Description.ToString().Equals("Fits: Everyone ♥", StringComparison.OrdinalIgnoreCase);
        eternalBonding.Rules.Add(new LateTabBundleRule(eternalBonding));

        var mogstation = Cat("Mogstation", 16);
        mogstation.Discriminator.ItemPredicate = item => FittingShopItemSet.Any(s => s.Items.Any(i => i.RowId == item.RowId));
        mogstation.Rules.Add(new LateTabBundleRule(mogstation));

        var uncategorized = new OutfitCategory("Unsorted", int.MinValue) { IsSyntheticBucket = true };
        var unobtainableBucket = new OutfitCategory("Unobtainable", int.MaxValue) { IsSyntheticBucket = true };

        OutfitCategory[] classifiable = [
            goldSaucer, pvp, tribes, gil, tradecraft, jobGear, eureka, occultCrescent,
            dungeons, raids, trials, vcDungeons, deepDungeons, fates, island, eternalBonding, mogstation,
        ];

        var uiTabs = new List<OutfitCategory> { uncategorized };
        uiTabs.AddRange(classifiable);
        uiTabs.Add(unobtainableBucket);

        return new Catalog(uiTabs, classifiable, pvp, dungeons, uncategorized, unobtainableBucket, chestLootProv, fateLootProv);
    }

    public static Dictionary<uint, SpecialShop> BuildSpecialShopByReceiveItemId()
        => SpecialShop.Where(s => s.RowId > 0 && !string.IsNullOrEmpty(s.Name.ToString()))
            .SelectMany(s => s.Item.SelectMany(item => item.ReceiveItems.Select(r => new { Shop = s, ItemId = r.Item.RowId })))
            .Where(x => x.ItemId > 0).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.First().Shop);
}
