using System.ComponentModel;
using GlamourLog.Services;

namespace GlamourLog.Windows.LogWindow;

// filter/sorting for the middle column. 
internal static class SetListFilterSort {
    public static List<GlamourSet> Apply(string searchTrimmed, List<GlamourSet> categoryRows, HashSet<GlamourSet> ownedSets, OwnershipSnapshot snap) {
        var rows = searchTrimmed.Length > 0 ? [.. Svc.Get<CatalogService>().GlamourSets] : categoryRows;

        if (C.HideCompleted)
            rows = [.. rows.Where(r => !ownedSets.Contains(r))];

        if (C.HideIncompatible)
            rows = [.. rows.Where(r => !r.IsIncompatible)];

        var hasPositiveFilters = C.HideNonPartials || C.HideUnaffordable || C.HideUnready || C.HideNoMarketboard;
        if (hasPositiveFilters) {
            HashSet<uint>? inventoryOnly = C.HideUnready ? snap.InventoryItemIds : null;
            rows = [.. rows.Where(r =>
                (!C.HideNonPartials || PassesStartedFilter(r, ownedSets, snap)) &&
                (!C.HideUnaffordable || PassesAffordableFilter(r, snap)) &&
                (!C.HideUnready || (inventoryOnly is not null && Svc.Get<OwnershipService>().HasContributablePieceInInventory(r, inventoryOnly, snap))) &&
                (!C.HideNoMarketboard || PassesTradeableFilter(r))
            )];
        }

        if (C.ShowOnlyMisplaced)
            rows = [.. rows.Where(r => Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(r, snap.OwnedItems, Svc.Get<CatalogService>().ArmoireItemIds, snap))];

        if (searchTrimmed.Length > 0)
            rows = [.. rows.Where(r => MatchesSearch(r, searchTrimmed))];

        return ApplySort(rows);
    }

    private static bool PassesStartedFilter(GlamourSet set, HashSet<GlamourSet> ownedSets, OwnershipSnapshot snap) {
        if (set.NonSetCabinetPiece)
            return !ownedSets.Contains(set) && Svc.Get<OwnershipService>().GetOwnedPieceCountForSet(set, snap.OwnedItems, snap) > 0;
        return Svc.Get<OwnershipService>().IsPartiallyCompleted(set, ownedSets, snap.OwnedItems, snap);
    }

    private static bool PassesAffordableFilter(GlamourSet set, OwnershipSnapshot snap) {
        if (set.NonSetCabinetPiece) {
            var missing = set.Items.Where(id => !snap.OwnedItems.Contains(id)).ToList();
            if (missing.Count == 0)
                return true;
            return missing.All(id => Svc.Get<CatalogService>().CostsLookup.GetItemCosts(id).Count == 0);
        }
        return Svc.Get<OwnershipService>().CanAffordAllMissingGearPieces(set, snap.OwnedItems);
    }

    private static bool PassesTradeableFilter(GlamourSet set) {
        if (set.NonSetCabinetPiece && set.Items.Count == 1)
            return !Item.GetRow(set.Items[0]).IsUntradable;
        return Svc.Get<OwnershipService>().IsMarketboardPurchasable(set);
    }

    private static bool MatchesSearch(GlamourSet set, string searchTrimmed)
        => set.Name.Contains(searchTrimmed, StringComparison.OrdinalIgnoreCase)
            || set.Items.Any(id => Item.GetRowRef(id) is { IsValid: true, Value.Name: var name } && name.ToString().Contains(searchTrimmed, StringComparison.OrdinalIgnoreCase));

    private static List<GlamourSet> ApplySort(List<GlamourSet> rows) {
        var asc = C.SetListSortDirection == ListSortDirection.Ascending;
        return C.SetListSortMode switch {
            GlamourSetSortMode.Alphabetical => asc
                ? [.. rows.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)]
                : [.. rows.OrderByDescending(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            GlamourSetSortMode.ItemLevel => asc
                ? [.. rows.OrderBy(s => s.SortItemLevel).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)]
                : [.. rows.OrderByDescending(s => s.SortItemLevel).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            GlamourSetSortMode.Patch => asc
                ? [.. rows.OrderBy(s => s.SortPatchNo).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)]
                : [.. rows.OrderByDescending(s => s.SortPatchNo).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            _ => rows,
        };
    }
}
