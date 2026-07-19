namespace GlamourLog;

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
        foreach (var itemId in ctx.ItemIds) {
            if (d.LateCostCurrencyItemIds.Count > 0) {
                foreach (var cost in Svc.Items.GetItemCosts(itemId)) {
                    if (d.LateCostCurrencyItemIds.Contains(cost.ItemId) && (d.CostAmount == null || d.CostAmount.Invoke(cost.Amount)))
                        return Owner.Name;
                }
            }
            if (d.ItemPredicate?.Invoke(Item.GetRow(itemId)) == true)
                return Owner.Name;
        }
        return null;
    }
}

// A glamour-log tab: either a real category matched by Rules, or a synthetic fallback tab (see IsSyntheticBucket).
// left panel categories
internal sealed class OutfitCategory(string name, int uiPriority) {
    public string Name { get; } = name; // ui label
    public int UiPriority { get; } = uiPriority; // sort order ascending
    public bool IsSyntheticBucket { get; init; } // this is for non-rule matched buckets like unsorted, misc armoire and unobtainable
    public List<IGlamourCategoryRule> Rules { get; } = []; // defines which sets land here
    public CategoryDiscriminator Discriminator { get; } = new();
}

// null name = didn't match a rule therefore it's either unsorted or unobtainable
internal readonly record struct ClassifyResult(string? CategoryName, bool IsUnobtainable);
