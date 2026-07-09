using System.ComponentModel;
using GlamourLog.Services;

namespace GlamourLog.Windows.LogWindow;

// filter/sorting for the middle column. 
internal static class SetListFilterSort {
    public static List<GlamourSet> Apply(string searchTrimmed, List<GlamourSet> categoryRows, OwnershipSnapshot snap) {
        var rows = categoryRows;

        if (C.HideCompleted)
            rows = [.. rows.Where(r => !snap.OwnedSets.Contains(r))];

        if (C.ShowOnlyCompleted)
            rows = [.. rows.Where(snap.OwnedSets.Contains)];

        if (C.HideIncompatible)
            rows = [.. rows.Where(r => !r.IsIncompatible)];

        if (C.HideSharedModels)
            rows = ApplySharedModelDedup(rows, snap);

        var hasPositiveFilters = C.HideNonPartials || C.HideUnaffordable || C.HideUnready || C.HideNoMarketboard;
        if (hasPositiveFilters) {
            rows = [.. rows.Where(r =>
                (!C.HideNonPartials || PassesStartedFilter(r, snap)) &&
                (!C.HideUnaffordable || PassesAffordableFilter(r, snap)) &&
                (!C.HideUnready || Svc.Get<OwnershipService>().HasContributablePieceInInventory(r, snap)) &&
                (!C.HideNoMarketboard || PassesTradeableFilter(r))
            )];
        }

        if (C.ShowOnlyMisplaced)
            rows = [.. rows.Where(r => Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(r, snap))];

        if (searchTrimmed.Length > 0)
            rows = [.. rows.Where(r => MatchesSearch(r, searchTrimmed))];

        return ApplySort(rows);
    }

    private static bool PassesStartedFilter(GlamourSet set, OwnershipSnapshot snap) {
        if (set.NonSetCabinetPiece)
            return !snap.OwnedSets.Contains(set) && Svc.Get<OwnershipService>().GetOwnedPieceCountForSet(set, snap) > 0;
        return Svc.Get<OwnershipService>().IsPartiallyCompleted(set, snap);
    }

    private static List<GlamourSet> ApplySharedModelDedup(List<GlamourSet> rows, OwnershipSnapshot snap) {
        if (rows.Count == 0)
            return rows;

        var keep = new HashSet<GlamourSet>();
        foreach (var group in rows.GroupBy(r => r.ModelSignature)) {
            var members = group.ToList();
            if (members[0].SharedModelGroupSize <= 1) {
                foreach (var set in members)
                    keep.Add(set);
                continue;
            }

            var active = members.Where(s => snap.OwnedSets.Contains(s) || PassesStartedFilter(s, snap)).ToList();
            if (active.Count > 0) {
                foreach (var set in active)
                    keep.Add(set);
            }
            else {
                keep.Add(members
                    .OrderByDescending(s => s.Items.Max(id => Item.GetRow(id).DyeCount))
                    .ThenBy(s => s.ItemId)
                    .First());
            }
        }

        return [.. rows.Where(keep.Contains)];
    }

    internal static bool IsVisibleInSetList(GlamourSet set, string searchTrimmed, List<GlamourSet> categoryRows, OwnershipSnapshot snap)
        => Apply(searchTrimmed, categoryRows, snap).Contains(set);

    private static bool PassesAffordableFilter(GlamourSet set, OwnershipSnapshot snap)
        => Svc.Get<OwnershipService>().CanAffordAllMissingGearPieces(set, snap);

    private static bool PassesTradeableFilter(GlamourSet set) {
        if (set.NonSetCabinetPiece && set.Items.Count == 1)
            return !Item.GetRow(set.Items[0]).IsUntradable;
        return set.Items.Any(itemId => !Item.GetRow(itemId).IsUntradable);
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
