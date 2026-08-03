using Dalamud.Game;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;
using System.Collections.Immutable;
using TerritoryIntendedUseEnum = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace GlamourLog;

internal sealed class Catalog {
    // these are all pre-pvp series pvp sets
    private static readonly ImmutableHashSet<uint> UnobtainableMirageRowIds = new HashSet<uint> { 45320, 45248, 45247, 45306, 45340, 45289, 45339, 45222, 45330, 45223, 45424, 45423 }.ToImmutableHashSet();

    public IReadOnlyList<OutfitCategory> UITabsInOrder { get; }
    public IReadOnlyList<OutfitCategory> ClassifiableCategories { get; }
    public OutfitCategory PvpSeriesAttire { get; }
    public OutfitCategory DungeonChest { get; }
    public OutfitCategory UncategorizedBucket { get; }
    public OutfitCategory MiscArmoireBucket { get; }

    private Catalog(
        IReadOnlyList<OutfitCategory> uiTabsInOrder,
        IReadOnlyList<OutfitCategory> classifiableCategories,
        OutfitCategory pvpSeriesAttire,
        OutfitCategory dungeonChest,
        OutfitCategory uncategorizedBucket,
        OutfitCategory miscArmoireBucket) {
        UITabsInOrder = uiTabsInOrder;
        ClassifiableCategories = classifiableCategories;
        PvpSeriesAttire = pvpSeriesAttire;
        DungeonChest = dungeonChest;
        UncategorizedBucket = uncategorizedBucket;
        MiscArmoireBucket = miscArmoireBucket;
    }

    internal static HashSet<uint> BuildCurrencyIdsFromCfcSupplemental<T>(string resourceName, Func<T, uint> itemIdSelector, Func<T, uint> cfcIdSelector, Func<T, bool>? rowFilter, params uint[] allowedContentTypes) where T : ICsv, new()
        => [.. Svc.Data.GetSupplemental<T>(resourceName)
            .Where(r => (rowFilter?.Invoke(r) ?? true) && itemIdSelector(r) != 0
            && cfcIdSelector(r) is not 0 and var cfcId && ContentFinderCondition.GetRowRef(cfcId) is { IsValid: true } cfc
            && allowedContentTypes.Contains(cfc.Value.ContentType.RowId)).Select(r => itemIdSelector(r))];

    // DungeonChestItem.ChestId is the key for DungeonChest.RowId for dungeons
    // raids use DungeonBossDrop for currencies
    internal static HashSet<uint> BuildDungeonChestPieceIdsFromSupplemental() {
        var chestByRowId = new Dictionary<uint, DungeonChest>();
        foreach (var chest in Svc.Data.GetSupplemental<DungeonChest>(CsvLoader.DungeonChestResourceName))
            chestByRowId[chest.RowId] = chest;

        var dungeonPieces = new HashSet<uint>();

        foreach (var itemRow in Svc.Data.GetSupplemental<DungeonChestItem>(CsvLoader.DungeonChestItemResourceName)) {
            if (itemRow.ItemId == 0) continue;
            if (!chestByRowId.TryGetValue(itemRow.ChestId, out _))
                continue;

            dungeonPieces.Add(itemRow.ItemId);
        }
        return dungeonPieces;
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

    public ClassifyResult ClassifySet(MirageStoreSetItem mirageRow, ReadOnlyCollection<uint> itemIds, SpecialShop? specialShopRow, byte clientPvpSeries) {
        var ctx = new ClassifyContext(mirageRow, itemIds, specialShopRow);
        foreach (var row in Svc.Data.GetSheet<PvPSeries>().Skip(1)) {
            if (!row.AttireItems.ContainsAll(itemIds)) continue;
            if (row.RowId == clientPvpSeries)
                return new ClassifyResult(PvpSeriesAttire.Name, false);
            // retired series with no shop costs — keep under PvP, mark unobtainable
            if (!itemIds.Any(Svc.Items.HasAnyCosts))
                return new ClassifyResult(PvpSeriesAttire.Name, true);
            if (ClassifyFromRules(ctx) is { } fromPvp)
                return new ClassifyResult(fromPvp, false);
            return new ClassifyResult(PvpSeriesAttire.Name, false);
        }
        if (ClassifyFromRules(ctx) is { } cat)
            return new ClassifyResult(cat, false);
        // pre-series PvP attire (before Series rewards existed)
        if (UnobtainableMirageRowIds.Contains(mirageRow.RowId))
            return new ClassifyResult(PvpSeriesAttire.Name, true);
        return new ClassifyResult(null, false);
    }

    public string GetDisplayCategoryName(string? categoryName)
        => categoryName ?? UncategorizedBucket.Name;

    // keep all-armoire sets if they still cost a dungeon chest piece (otherwise they'd vanish from the log)
    public bool IncludeAfterArmoireFilter(IReadOnlyList<uint> items, HashSet<uint> armoireItems) {
        if (items.Count == 0)
            return false;
        if (items.Any(id => !armoireItems.Contains(id)))
            return true;
        var chest = DungeonChest.Discriminator.PieceOrCostItemIds;
        return chest is { Count: not 0 } && items.Any(id => Svc.Items.GetItemCosts(id).Any(c => chest.Contains(c.ItemId)));
    }

    // placeholder until Build runs after login since Tradecraft needs CurrencyManager
    internal static Catalog CreateEmptyStub() {
        var uncategorized = new OutfitCategory("Unsorted", int.MinValue) { IsSyntheticBucket = true };
        var miscArmoire = new OutfitCategory("Misc Armoire", 17) { IsSyntheticBucket = true };
        var pvp = new OutfitCategory("PvP", 1);
        var dungeons = new OutfitCategory("Dungeons", 8);
        OutfitCategory[] classifiable = [];
        var uiTabs = new List<OutfitCategory> { uncategorized, miscArmoire };
        return new Catalog(uiTabs, classifiable, pvp, dungeons, uncategorized, miscArmoire);
    }

    // tradecraftCurrencyItemIds must be resolved via CurrencyManager after login. It's not populated before
    public static Catalog Build(IReadOnlyList<uint> tradecraftCurrencyItemIds) {
        var dungeonChestPieces = BuildDungeonChestPieceIdsFromSupplemental();

        static OutfitCategory Cat(string name, int uiP) => new(name, uiP);

        var goldSaucer = Cat("Gold Saucer", 0);
        goldSaucer.Discriminator.TerritoryIntendedUseIds.AddRange([(uint)TerritoryIntendedUseEnum.GoldSaucer, (uint)TerritoryIntendedUseEnum.Blunderville]);
        goldSaucer.Rules.Add(new LateTabBundleRule(goldSaucer));

        var pvp = Cat("PvP", 1);
        pvp.Discriminator.LateCostCurrencyItemIds.AddRange([25, 36656, 40479]); // wolf marks, trophy crystals, commendation crystals
        pvp.Rules.Add(new LateTabBundleRule(pvp));

        var tribes = Cat("Tribes", 2);
        tribes.Discriminator.LateCostCurrencyItemIds.AddRange(BeastTribe.Where(r => r.CurrencyItem.RowId != 0).Select(r => r.CurrencyItem.RowId));
        tribes.Discriminator.QuestJournalMatches.AddRange([
            new(QuestJournalKind.JournalSection, 4), // Allied Society Quests (ARR–EW)
            new(QuestJournalKind.JournalSection, 5), // Allied Society Quests (Dawntrail)
        ]);
        tribes.Rules.Add(new LateTabBundleRule(tribes));

        var jobGear = Cat("Job Gear", 5);
        jobGear.Discriminator.SpecialShopPredicate = shop => shop.UseCurrencyType == 8 && shop.Quest.RowId > 0;
        jobGear.Discriminator.QuestJournalMatches.Add(new(QuestJournalKind.JournalSection, 6)); // Class & Job Quests
        jobGear.Rules.Add(new LateTabBundleRule(jobGear));

        var gil = Cat("Gil", 3);
        gil.Discriminator.LateCostCurrencyItemIds.Add(1);
        gil.Discriminator.CostAmount = amount => amount > 0;
        gil.Rules.Add(new LateTabBundleRule(gil));

        var tradecraft = Cat("Tradecraft", 4);
        tradecraft.Discriminator.LateCostCurrencyItemIds.AddRange(tradecraftCurrencyItemIds);
        tradecraft.Rules.Add(new LateTabBundleRule(tradecraft));

        var forays = Cat("Forays", 6);
        forays.Discriminator.TerritoryIntendedUseIds.AddRange([(uint)TerritoryIntendedUseEnum.Eureka, (uint)TerritoryIntendedUseEnum.Bozja, (uint)TerritoryIntendedUseEnum.OccultCrescent]);
        forays.Rules.Add(new LateTabBundleRule(forays));

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
        raids.Discriminator.LateCostCurrencyItemIds.AddRange([22599, 23383, 47100]); // rathalos scale, rathalos+ scale, guardian scale
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
        vcDungeons.Discriminator.LateCostCurrencyItemIds.AddRange([38533, 39884, 41078, 50434]); // potsherds
        vcDungeons.Rules.Add(new LateTabBundleRule(vcDungeons));

        var deepDungeons = Cat("Deep Dungeons", 12);
        deepDungeons.Discriminator.LateCostCurrencyItemIds.AddRange([15422, 23164, 46186]); // the other potsherds, illumed glass
        deepDungeons.Rules.Add(new LateTabBundleRule(deepDungeons));

        var fates = Cat("Fates", 13);
        fates.Discriminator.LateCostCurrencyItemIds.AddRange([12252, 27972, 36634, 41804]); // TODO: probably something in allaganlib for drops from fate
        fates.Rules.Add(new LateTabBundleRule(fates));

        var island = Cat("Island Sanctuary", 14);
        island.Discriminator.LateCostCurrencyItemIds.AddRange([37549, 37550, 41668]); // seafarer's/islander's cowries, felicitous token
        island.Discriminator.TerritoryIntendedUseIds.Add((uint)TerritoryIntendedUseEnum.IslandSanctuary); // this does nothing since the vendors aren't actually in the island sanctuary territory
        island.Discriminator.QuestJournalMatches.Add(new(QuestJournalKind.JournalGenre, 107)); // Island Sanctuary Quests
        island.Rules.Add(new LateTabBundleRule(island));

        var eternalBonding = Cat("Eternal Bonding", 15);
        eternalBonding.Discriminator.ItemPredicate = item => item.WithLanguage(ClientLanguage.English).Description.ToString().Equals("Fits: Everyone ♥", StringComparison.OrdinalIgnoreCase);
        eternalBonding.Rules.Add(new LateTabBundleRule(eternalBonding));

        var mogstation = Cat("Mogstation", 16);
        mogstation.Discriminator.ItemPredicate = item => FittingShopItemSet.Any(s => s.Items.Any(i => i.RowId == item.RowId));
        mogstation.Rules.Add(new LateTabBundleRule(mogstation));

        var uncategorized = new OutfitCategory("Unsorted", int.MinValue) { IsSyntheticBucket = true };
        var miscArmoire = new OutfitCategory("Misc Armoire", 17) { IsSyntheticBucket = true };

        OutfitCategory[] classifiable = [
            goldSaucer, pvp, tribes, jobGear, island, gil, tradecraft, forays,
            dungeons, raids, trials, vcDungeons, deepDungeons, fates, eternalBonding, mogstation,
        ];

        var uiTabs = new List<OutfitCategory> { uncategorized };
        uiTabs.AddRange(classifiable);
        uiTabs.Add(miscArmoire);

        return new Catalog(uiTabs, classifiable, pvp, dungeons, uncategorized, miscArmoire);
    }

    public static Dictionary<uint, SpecialShop> BuildSpecialShopByReceiveItemId()
        => SpecialShop.Where(s => s.RowId > 0 && !string.IsNullOrEmpty(s.Name.ToString()))
            .SelectMany(s => s.Item.SelectMany(item => item.ReceiveItems.Select(r => new { Shop = s, ItemId = r.Item.RowId })))
            .Where(x => x.ItemId > 0).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.First().Shop);
}
