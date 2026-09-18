using System.ComponentModel;
using GlamourLog.Services;

namespace GlamourLog.Windows.LogWindow;

// filter/sorting for the middle column. 
internal static class SetListFilterSort {
    public static List<GlamourSet> Apply(string searchTrimmed, IReadOnlyList<GlamourSet> categoryRows, OwnershipQuery q, SetListFilterState filters) {
        IEnumerable<GlamourSet> rows = categoryRows;

        rows = rows.Where(r =>
            Passes(C.FilterCompleted, q.For(r).IsComplete) &&
            Passes(C.FilterIncompatible, r.IsIncompatible) &&
            Passes(C.FilterUnobtainable, r.IsUnobtainable) &&
            Passes(C.FilterMogstation, r.IsMogstation));

        if (filters.UseCustomPatches)
            rows = rows.Where(r => filters.PatchNumbers.Contains(r.PatchNo));
        else if (filters.ExpansionRowIds.Count > 0)
            rows = rows.Where(r => r.PatchNo >= 2m && filters.ExpansionRowIds.Contains((uint)decimal.Truncate(r.PatchNo) - 2));

        if (filters.ClassJobIds.Count > 0)
            rows = rows.Where(set => PassesClassJobFilter(set, filters.ClassJobIds, filters.PartialClassJobMatch));

        if (filters.Source is { } source) {
            var catalog = CatalogService.Get();
            rows = rows.Where(set => catalog.SetMatchesSourceFilter(set, source, filters.SubSource));
        }

        rows = rows.Where(r => Passes(C.FilterSharedModels, r.SharedModelGroupSize > 1));

        rows = rows.Where(r => {
            var s = q.For(r);
            return Passes(C.FilterStarted, s.IsPartial)
                && Passes(C.FilterAffordable, s.CanAffordMissing)
                && Passes(C.FilterContributable, s.HasContributableInventoryPiece)
                && Passes(C.FilterTradeable, PassesTradeableFilter(r))
                && Passes(C.FilterArmoire, s.IsArmoireEligible)
                && Passes(C.FilterMisplaced, s.ArmoireMisplaced);
        });

        if (filters.CurrencyItemId != 0) {
            var catalog = CatalogService.Get();
            rows = rows.Where(r => catalog.SetUsesCurrencyFilter(r, filters.CurrencyItemId));
        }

        if (searchTrimmed.Length > 0)
            rows = rows.Where(r => MatchesSearch(r, searchTrimmed));

        return ApplySort(rows);
    }

    internal static bool IsVisibleInSetList(GlamourSet set, string searchTrimmed, IReadOnlyList<GlamourSet> categoryRows, OwnershipQuery q, SetListFilterState filters)
        => Apply(searchTrimmed, categoryRows, q, filters).Contains(set);

    internal static bool IsMogstationSet(GlamourSet set) => set.IsMogstation;

    internal static bool IsMogstationItem(uint itemId)
        => FittingShopItemSet.Any(s => s.Items.Any(i => i.RowId == itemId)) || FittingShopCategoryItem.Any(s => s.Item.RowId == itemId);

    private static bool PassesTradeableFilter(GlamourSet set) {
        if (set.NonSetCabinetPiece && set.Items.Count == 1)
            return !Item.GetRow(set.Items[0]).IsUntradable;
        return set.Items.Any(itemId => !Item.GetRow(itemId).IsUntradable);
    }

    private static bool Passes(FilterType filter, bool matches)
        => filter switch {
            FilterType.Only => matches,
            FilterType.Exclude => !matches,
            _ => true,
        };

    private static bool PassesClassJobFilter(GlamourSet set, IReadOnlyCollection<uint> classJobIds, bool partialMatch) {
        if (partialMatch)
            return classJobIds.Any(classJobId =>
                set.Items.All(itemId => Item.GetRow(itemId).ClassJobCategory.Value.ContainsJob(ClassJob.GetRow(classJobId))));

        var selected = classJobIds.ToHashSet();
        return set.Items.All(itemId => {
            var category = Item.GetRow(itemId).ClassJobCategory.Value;
            return ClassJob.All(job => selected.Contains(job.RowId) == category.ContainsJob(job));
        });
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
