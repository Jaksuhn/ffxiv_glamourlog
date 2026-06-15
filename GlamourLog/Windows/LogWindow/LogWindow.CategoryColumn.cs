using GlamourLog.Nodes;
using GlamourLog.Services;

namespace GlamourLog;

internal partial class LogWindow {
    private List<string> BuildOrderedCategoryPaneList() {
        var r = new List<string> { AllCategoryId, Svc.Get<CatalogService>().UncategorizedTab.Name };
        foreach (var (category, _) in Svc.Get<CatalogService>().OutfitCategories.Select((c, ix) => (c, ix)).OrderBy(x => x.c.UiPriority).ThenBy(x => x.ix))
            r.Add(category.Name);
        r.Add(Svc.Get<CatalogService>().MiscArmoireTab.Name);
        r.Add(Svc.Get<CatalogService>().UnobtainableTab.Name);
        return r;
    }

    private void RebuildCategoryButtonsFromPaneOrder() {
        _categoryColumn?.RebuildFromPaneOrder(_categoryPaneOrder, _selectedCategoryId);
    }

    private List<GlamourSet> CategoryRows(string categoryId)
        => categoryId == AllCategoryId
            ? [.. Svc.Get<CatalogService>().GlamourSets]
            : Svc.Get<CatalogService>().GlamourSetsByCategory.TryGetValue(categoryId, out var list) ? list : [];

    private void SyncCategoryPaneToDataVersion() {
        if (_categoryColumn is null)
            return;

        var catalog = Svc.Get<CatalogService>();
        var dataVersion = catalog.DataVersion;
        if (_lastDataVersion == dataVersion)
            return;

        _lastDataVersion = dataVersion;

        var newOrder = BuildOrderedCategoryPaneList();
        if (CategoryPaneOrderMatches(newOrder))
            return;

        _categoryPaneOrder.Clear();
        _categoryPaneOrder.AddRange(newOrder);
        _pendingCategoryPaneRebuild = true;
        if (!_categoryPaneOrder.Contains(_selectedCategoryId))
            _selectedCategoryId = catalog.UncategorizedTab.Name;
        SetList?.ResetScroll();
    }

    private bool CategoryPaneOrderMatches(List<string> newOrder) {
        if (_categoryPaneOrder.Count != newOrder.Count)
            return false;
        for (var i = 0; i < newOrder.Count; i++) {
            if (_categoryPaneOrder[i] != newOrder[i])
                return false;
        }
        return true;
    }
}
