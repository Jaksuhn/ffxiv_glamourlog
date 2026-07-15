namespace GlamourLog;

/// <summary> All per-tab matching inputs in one place.</summary>
internal sealed class CategoryDiscriminator {
    /// <summary> Supplemental chest / currency ids: phases 0–1 (piece in set, cost currency in set) and armoire filter.</summary>
    public HashSet<uint>? PieceOrCostItemIds { get; set; }

    /// <summary> Late-phase (phase 2): cost rows whose <c>ItemId</c> is in this list; optional <see cref="CostAmount"/> filters <c>Amount</c>.</summary>
    public List<uint> LateCostCurrencyItemIds { get; } = [];

    public Func<uint, bool>? CostAmount { get; set; }
    public Func<Item, bool>? ItemPredicate { get; set; }
    public Func<SpecialShop, bool>? SpecialShopPredicate { get; set; }
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

/// <summary> One classifiable bucket or synthetic UI bucket. <see cref="Name"/> is both identity and label.</summary>
internal sealed class OutfitCategory(string name, int uiPriority) {
    public string Name { get; } = name;
    public int UiPriority { get; } = uiPriority;
    public bool IsSyntheticBucket { get; init; }
    public List<IGlamourCategoryRule> Rules { get; } = [];
    public CategoryDiscriminator Discriminator { get; } = new();
}

/// <summary> Mirrors <see cref="GlamourSet.CategoryName"/> + <see cref="GlamourSet.IsUnobtainable"/>.</summary>
internal readonly record struct ClassifyResult(string? CategoryName, bool IsUnobtainable);
