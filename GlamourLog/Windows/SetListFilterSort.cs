using System.ComponentModel;
using GlamourLog.Services;

namespace GlamourLog.Windows;

// filter/sorting for the middle column. 
internal static class SetListFilterSort {
    public static List<GlamourSet> Apply(string searchTrimmed, List<GlamourSet> categoryRows, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        var rows = searchTrimmed.Length > 0 ? [.. Svc.Get<CatalogService>().GlamourSets] : categoryRows;

        if (C.HideCompleted)
            rows = [.. rows.Where(r => !ownedSets.Contains(r))];

        var hasPositiveFilters = C.HideNonPartials || C.HideUnaffordable || C.HideUnready || C.HideNoMarketboard;
        if (hasPositiveFilters) {
            var inventoryOnly = C.HideUnready ? Svc.Get<OwnershipService>().GetInventoryItemsOnly() : null;
            rows = [.. rows.Where(r =>
                (!C.HideNonPartials || Svc.Get<OwnershipService>().IsPartiallyCompleted(r, ownedSets, ownedItems)) &&
                (!C.HideUnaffordable || Svc.Get<OwnershipService>().CanAffordAllMissingGearPieces(r, ownedItems)) &&
                (!C.HideUnready || (inventoryOnly is not null && Svc.Get<OwnershipService>().HasContributablePieceInInventory(r, inventoryOnly))) &&
                (!C.HideNoMarketboard || Svc.Get<OwnershipService>().IsMarketboardPurchasable(r))
            )];
        }

        if (C.ShowOnlyMisplaced)
            rows = [.. rows.Where(r => Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(r, ownedItems, Svc.Get<CatalogService>().ArmoireItemIds))];

        if (searchTrimmed.Length > 0)
            rows = [.. rows.Where(r => MatchesSearch(r, searchTrimmed))];

        return ApplySort(rows);
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
