using System.ComponentModel;
using GlamourLog.Services;

namespace GlamourLog.Windows.LogWindow;

// filter/sorting for the middle column. 
internal static class SetListFilterSort {
    public static List<GlamourSet> Apply(string searchTrimmed, IReadOnlyList<GlamourSet> categoryRows, OwnershipQuery q, uint currencyFilterItemId = 0) {
        IEnumerable<GlamourSet> rows = categoryRows;

        if (C.HideCompleted)
            rows = rows.Where(r => !q.For(r).IsComplete);

        if (C.ShowOnlyCompleted)
            rows = rows.Where(r => q.For(r).IsComplete);

        if (C.HideIncompatible)
            rows = rows.Where(r => !r.IsIncompatible);

        if (C.HideUnobtainable)
            rows = rows.Where(r => !r.IsUnobtainable || q.For(r).IsComplete);

        if (C.HideMogstation)
            rows = rows.Where(r => !r.IsMogstation);

        if (C.HideSharedModels)
            rows = HideSharedModelSets([.. rows], q);

        // Hide* are really"show only"
        var hasPositiveFilters = C.HideNonPartials || C.HideUnaffordable || C.HideUnready || C.HideNoMarketboard;
        if (hasPositiveFilters) {
            rows = rows.Where(r => {
                var s = q.For(r);
                return (!C.HideNonPartials || s.IsPartial)
                    && (!C.HideUnaffordable || s.CanAffordMissing)
                    && (!C.HideUnready || s.HasContributableInventoryPiece)
                    && (!C.HideNoMarketboard || PassesTradeableFilter(r));
            });
        }

        if (C.ShowOnlyMisplaced)
            rows = rows.Where(r => q.For(r).ArmoireMisplaced);

        if (currencyFilterItemId != 0) {
            var catalog = CatalogService.Get();
            rows = rows.Where(r => catalog.SetUsesCurrencyFilter(r, currencyFilterItemId));
        }

        if (searchTrimmed.Length > 0)
            rows = rows.Where(r => MatchesSearch(r, searchTrimmed));

        return ApplySort(rows);
    }

    private static List<GlamourSet> HideSharedModelSets(List<GlamourSet> rows, OwnershipQuery q) {
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

            var active = members.Where(s => {
                var status = q.For(s);
                return status.IsComplete || status.IsPartial;
            }).ToList();
            if (active.Count > 0) {
                foreach (var set in active)
                    keep.Add(set);
            }
            else {
                // none started -> keep the most dyeable version
                keep.Add(members.OrderByDescending(s => s.Items.Max(id => Item.GetRow(id).DyeCount)).ThenBy(s => s.ItemId).First());
            }
        }

        return [.. rows.Where(keep.Contains)];
    }

    internal static bool IsVisibleInSetList(GlamourSet set, string searchTrimmed, IReadOnlyList<GlamourSet> categoryRows, OwnershipQuery q, uint currencyFilterItemId = 0)
        => Apply(searchTrimmed, categoryRows, q, currencyFilterItemId).Contains(set);

    internal static bool IsMogstationSet(GlamourSet set) => set.IsMogstation;

    internal static bool IsMogstationItem(uint itemId)
        => FittingShopItemSet.Any(s => s.Items.Any(i => i.RowId == itemId)) || FittingShopCategoryItem.Any(s => s.Item.RowId == itemId);

    private static bool PassesTradeableFilter(GlamourSet set) {
        if (set.NonSetCabinetPiece && set.Items.Count == 1)
            return !Item.GetRow(set.Items[0]).IsUntradable;
        return set.Items.Any(itemId => !Item.GetRow(itemId).IsUntradable);
    }

    private static bool MatchesSearch(GlamourSet set, string searchTrimmed)
        => set.Name.Contains(searchTrimmed, StringComparison.OrdinalIgnoreCase)
            || set.Items.Any(id => Item.GetRowRef(id) is { IsValid: true, Value.Name: var name } && name.ToString().Contains(searchTrimmed, StringComparison.OrdinalIgnoreCase));

    private static List<GlamourSet> ApplySort(IEnumerable<GlamourSet> rows) {
        var asc = C.SetListSortDirection == ListSortDirection.Ascending;
        return C.SetListSortMode switch {
            GlamourSetSortMode.Alphabetical => asc
                ? [.. rows.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)]
                : [.. rows.OrderByDescending(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            GlamourSetSortMode.ItemLevel => asc
                ? [.. rows.OrderBy(s => s.ItemLevel).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)]
                : [.. rows.OrderByDescending(s => s.ItemLevel).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            GlamourSetSortMode.Patch => asc
                ? [.. rows.OrderBy(s => s.PatchNo).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)]
                : [.. rows.OrderByDescending(s => s.PatchNo).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            _ => rows as List<GlamourSet> ?? [.. rows],
        };
    }
}
