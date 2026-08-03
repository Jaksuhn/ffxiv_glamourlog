using AllaganLib.GameSheets.ItemSources;

namespace GlamourLog;

internal enum QuestJournalKind {
    JournalSection,
    JournalGenre,
}

internal readonly record struct QuestJournalMatch(QuestJournalKind Kind, uint Id);

// various predicates for defining sets within a category
internal sealed class CategoryDiscriminator {
    // pass 0 is PieceInTabItemSetRule -> match if any piece in the set’s item list is in this set
    // pass 1 is CostCurrencyInTabItemSetRule -> match if any piece’s shop/exchange cost item id is in this set
    // only used by dungeons so far
    public HashSet<uint>? PieceOrCostItemIds { get; set; }
    public List<uint> LateCostCurrencyItemIds { get; } = []; // match if any set piece has a cost whose item id is in this list (and, if set, CostAmount passes).
    public Func<uint, bool>? CostAmount { get; set; } // extra filter for LateCostCurrencyItemIds to exclude free gil items
    public Func<Item, bool>? ItemPredicate { get; set; } // extra filter for misc matching, e.g. eternal bonding and mogstation
    public Func<SpecialShop, bool>? SpecialShopPredicate { get; set; } // like item predicate but for special shop
    public List<QuestJournalMatch> QuestJournalMatches { get; } = []; // match if any piece has a quest source with a matching quest category
    public List<uint> TerritoryIntendedUseIds { get; } = []; // match if any piece's shop source resolves to a territory with this IntendedUse
}

internal readonly record struct ClassifyContext(MirageStoreSetItem MirageRow, ReadOnlyCollection<uint> ItemIds, SpecialShop? SpecialShopRow);

internal interface IGlamourCategoryRule {
    int Phase { get; }
    OutfitCategory Owner { get; }
    string? TryMatch(ClassifyContext ctx);
}

internal sealed class PieceInTabItemSetRule(OutfitCategory owner) : IGlamourCategoryRule {
    public OutfitCategory Owner => owner;
    public int Phase => 0;
    public string? TryMatch(ClassifyContext ctx) {
        var set = Owner.Discriminator.PieceOrCostItemIds;
        if (set is not { Count: > 0 }) return null;
        foreach (var itemId in ctx.ItemIds) {
            if (set.Contains(itemId))
                return Owner.Name;
        }
        return null;
    }
}

internal sealed class CostCurrencyInTabItemSetRule(OutfitCategory owner) : IGlamourCategoryRule {
    public OutfitCategory Owner => owner;
    public int Phase => 1;
    public string? TryMatch(ClassifyContext ctx) {
        var set = Owner.Discriminator.PieceOrCostItemIds;
        if (set is not { Count: > 0 }) return null;
        foreach (var itemId in ctx.ItemIds) {
            foreach (var cost in Svc.Items.GetItemCosts(itemId)) {
                if (set.Contains(cost.ItemId))
                    return Owner.Name;
            }
        }
        return null;
    }
}

internal sealed class LateTabBundleRule(OutfitCategory owner) : IGlamourCategoryRule {
    public OutfitCategory Owner => owner;
    public int Phase => 2;
    public string? TryMatch(ClassifyContext ctx) {
        var d = Owner.Discriminator;
        if (ctx.SpecialShopRow is { } shop && d.SpecialShopPredicate?.Invoke(shop) == true)
            return Owner.Name;
        var checkQuest = d.QuestJournalMatches.Count > 0;
        var checkTerritory = d.TerritoryIntendedUseIds.Count > 0;
        foreach (var itemId in ctx.ItemIds) {
            if (d.LateCostCurrencyItemIds.Count > 0) {
                foreach (var cost in Svc.Items.GetItemCosts(itemId)) {
                    if (d.LateCostCurrencyItemIds.Contains(cost.ItemId) && (d.CostAmount == null || d.CostAmount.Invoke(cost.Amount)))
                        return Owner.Name;
                }
            }
            if (d.ItemPredicate?.Invoke(Item.GetRow(itemId)) == true)
                return Owner.Name;
            if ((checkQuest || checkTerritory) && MatchesSourcePredicates(itemId, d, checkQuest, checkTerritory))
                return Owner.Name;
        }
        return null;
    }

    private static bool MatchesSourcePredicates(uint itemId, CategoryDiscriminator d, bool checkQuest, bool checkTerritory) {
        if (Svc.SheetManager.ItemInfoCache.GetItemSources(itemId) is not { Count: > 0 } list)
            return false;
        foreach (var src in list) {
            if (checkQuest && src is ItemQuestSource qs && !IsOptionalQuestReward(qs, itemId) && MatchesQuestJournal(qs, d.QuestJournalMatches))
                return true;
            if (checkTerritory && src.MapIds is { Count: > 0 } mapIds) {
                foreach (var mapId in mapIds) {
                    if (TryTerritoryIntendedUse(mapId) is { } use && d.TerritoryIntendedUseIds.Contains(use))
                        return true;
                }
            }
        }
        return false;
    }

    // "choose one" quest chest options are marked optional in AllaganLib; skip those for journal classification
    // because it doesn't feel right to classify something if you can't get the full set that way. Otherwise some DoH/L scrip items end up in Job Gear
    private static bool IsOptionalQuestReward(ItemQuestSource qs, uint itemId)
        => qs.RewardItems.Any(r => r.ItemId == itemId && r.IsOptional == true);

    private static bool MatchesQuestJournal(ItemQuestSource qs, List<QuestJournalMatch> matches) {
        if (qs.Quest is not { RowId: not 0, IsValid: true, Value.JournalGenre: { IsValid: true, RowId: var genreId, Value: var genre } })
            return false;
        var sectionId = genre.JournalCategory is { IsValid: true, Value.JournalSection.RowId: var sid } ? sid : (uint?)null;
        return matches.Any(m => m.Kind switch {
            QuestJournalKind.JournalGenre => genreId == m.Id,
            QuestJournalKind.JournalSection => sectionId == m.Id,
            _ => false,
        });
    }

    private static uint? TryTerritoryIntendedUse(uint mapId) {
        if (Map.GetRowRef(mapId) is not { IsValid: true, Value.TerritoryType: { IsValid: true, Value: var territory } })
            return null;
        return territory.TerritoryIntendedUse.RowId;
    }
}

// A glamour-log tab: either a real category matched by Rules, or a synthetic fallback tab (see IsSyntheticBucket).
// left panel categories
internal sealed class OutfitCategory(string name, int uiPriority) {
    public string Name { get; } = name; // ui label
    public int UiPriority { get; } = uiPriority; // sort order ascending
    public bool IsSyntheticBucket { get; init; } // this is for non-rule matched buckets like unsorted and misc armoire
    public List<IGlamourCategoryRule> Rules { get; } = []; // defines which sets land here
    public CategoryDiscriminator Discriminator { get; } = new();
}

// null name = didn't match a rule therefore it's unsorted (and may still be marked unobtainable)
internal readonly record struct ClassifyResult(string? CategoryName, bool IsUnobtainable);
