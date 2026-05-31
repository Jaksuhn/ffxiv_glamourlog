using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog.Services;

internal sealed class CatalogService : IDisposable {
    private bool _disposed;

    internal ReadOnlyCollection<GlamourSet> GlamourSets { get; private set; } = Array.Empty<GlamourSet>().ToList().AsReadOnly();
    internal Dictionary<string, List<GlamourSet>> GlamourSetsByCategory { get; } = [];
    internal ItemCostLookup CostsLookup { get; } = new();
    internal HashSet<uint> ArmoireItemIds { get; private set; } = [];
    private HashSet<uint> _costCurrencyItemIds = [];
    private Dictionary<SetModelSignature, List<GlamourSet>> _sharedModelGroups = [];
    private Dictionary<ItemModelInfo, List<uint>> _sharedModelItemGroups = [];

    private readonly Lock _glamourDataLock = new();
    private readonly Lock _catalogRequestLock = new();
    private Catalog _catalog = Catalog.CreateEmptyStub();
    private volatile bool _catalogBuilt;
    private int _pendingListRefresh;
    private CancellationTokenSource? _catalogCts;

    internal int DataVersion { get; private set; }

    public CatalogService() {
        Svc.ClientState.Login += OnClientLogin;

        if (Svc.ClientState.IsLoggedIn) {
            lock (_glamourDataLock)
                _catalogBuilt = false;
            RequestCatalogBuild();
        }
    }

    public void Dispose() {
        if (_disposed)
            return;
        _disposed = true;
        Svc.ClientState.Login -= OnClientLogin;
        _catalogCts?.Cancel();
        _catalogCts?.Dispose();
        _catalogCts = null;
        lock (_glamourDataLock) {
            _catalogBuilt = false;
            _costCurrencyItemIds = [];
        }
    }

    private void OnClientLogin() {
        lock (_glamourDataLock)
            _catalogBuilt = false;
        RequestCatalogBuild();
    }

    internal void OnArmoireChanged() {
        lock (_glamourDataLock)
            _catalogBuilt = false;
        RequestCatalogBuild();
    }

    private void RequestCatalogBuild() {
        if (_catalogBuilt)
            return;
        lock (_catalogRequestLock) {
            if (_catalogBuilt)
                return;
            _catalogCts?.Cancel();
            _catalogCts?.Dispose();
            _catalogCts = new CancellationTokenSource();
            var token = _catalogCts.Token;
            _ = Task.Run(() => RunCatalogBuild(token), token);
        }
    }

    private void RunCatalogBuild(CancellationToken token) {
        try {
            token.ThrowIfCancellationRequested();
            var built = CatalogBuilder.Run(CostsLookup);
            token.ThrowIfCancellationRequested();

            lock (_glamourDataLock) {
                token.ThrowIfCancellationRequested();
                _catalog = built.Catalog;
                ArmoireItemIds = built.ArmoireItemIds;
                GlamourSets = built.Sets;
                GlamourSetsByCategory.Clear();
                foreach (var group in GlamourSets.GroupBy(s => _catalog.BucketKey(new ClassifyResult(s.CategoryName, s.IsUnobtainable))))
                    GlamourSetsByCategory[group.Key] = [.. group];
                _sharedModelGroups = GlamourSets
                    .GroupBy(s => s.ModelSignature)
                    .ToDictionary(g => g.Key, g => g.OrderBy(s => s.ItemId).ToList());
                _sharedModelItemGroups = GlamourSets
                    .SelectMany(s => s.Items)
                    .Distinct()
                    .GroupBy(static itemId => (ItemModelInfo)itemId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(id => id).ToList());
                LogMissingMirageSets();
                DataVersion++;
                _catalogBuilt = true;
            }
            _costCurrencyItemIds = BuildAllPrimaryCostCurrencyIds();
            Interlocked.Exchange(ref _pendingListRefresh, 1);
            Svc.Get<WindowsService>().RefreshLogWindow();
        }
        catch (OperationCanceledException) {
            // cancelled by logout / disable / superseded build
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"{nameof(CatalogService)} catalog build");
        }
    }

    internal bool CatalogReady => _catalogBuilt;

    internal bool IsKnownCostCurrency(uint itemId) => itemId != 0 && _catalogBuilt && _costCurrencyItemIds.Contains(itemId);

    internal bool TryConsumePendingListRefresh() => Interlocked.Exchange(ref _pendingListRefresh, 0) != 0;

    internal void MarkLogWindowDirty() => Svc.Get<WindowsService>().RefreshLogWindow();

    /// <summary> Inventory / dresser changes that affect ownership or counts shown in the main log window. </summary>
    internal void NotifyDisplayedOwnershipMayHaveChanged() => Svc.Get<WindowsService>().RefreshLogWindow();

    /// <summary> Real outfit tabs (excludes synthetic uncategorized / unobtainable rows).</summary>
    internal IReadOnlyList<OutfitCategory> OutfitCategories => _catalog.ClassifiableCategories;
    internal OutfitCategory UncategorizedTab => _catalog.UncategorizedBucket;
    internal OutfitCategory MiscArmoireTab => _catalog.MiscArmoireBucket;
    internal OutfitCategory UnobtainableTab => _catalog.UnobtainableBucket;

    internal string? CategoryNameForPrimaryCostLookup(GlamourSet set) {
        if (set.IsUnobtainable || set.CategoryName is null)
            return null;
        lock (_glamourDataLock) {
            if (set.CategoryName == _catalog.UncategorizedBucket.Name
                || set.CategoryName == _catalog.MiscArmoireBucket.Name
                || set.CategoryName == _catalog.UnobtainableBucket.Name
                || set.NonSetCabinetPiece)
                return null;
            return set.CategoryName;
        }
    }

    /// <summary> Item ids whose sources/costs should be shown for the Sources panel (set pieces + primary currencies for the current scope).</summary>
    internal IReadOnlyCollection<uint> GetSourceScopeItemIds(GlamourSet set, uint? costScopePieceItemId) {
        lock (_glamourDataLock)
            return [.. BuildSourceScopeItemIds(set, costScopePieceItemId)];
    }

    private HashSet<uint> BuildSourceScopeItemIds(GlamourSet set, uint? costScopePieceItemId) {
        var cat = CategoryNameForPrimaryCostLookup(set);
        var itemIds = new HashSet<uint>();
        if (costScopePieceItemId is { } only) {
            itemIds.Add(only);
            foreach (var c in GetPrimaryItemCosts(only, cat))
                if (c.ItemId != 0)
                    itemIds.Add(c.ItemId);
        }
        else {
            foreach (var i in set.Items) {
                itemIds.Add(i);
                foreach (var c in GetPrimaryItemCosts(i, cat))
                    if (c.ItemId != 0)
                        itemIds.Add(c.ItemId);
            }
        }
        return itemIds;
    }

    internal List<(uint ItemId, uint Amount)> GetPrimaryItemCosts(uint itemId, string? categoryNameForDiscriminator) {
        var costs = CostsLookup.GetItemCosts(itemId);
        if (costs.Count == 0)
            return [];
        if (!string.IsNullOrEmpty(categoryNameForDiscriminator)) {
            lock (_glamourDataLock) {
                foreach (var cat in _catalog.ClassifiableCategories) {
                    if (cat.Name != categoryNameForDiscriminator)
                        continue;
                    var late = cat.Discriminator.LateCostCurrencyItemIds;
                    if (late.Count > 0) {
                        var pinnedLate = costs.Where(c => late.Contains(c.ItemId)).ToList();
                        if (pinnedLate.Count > 0)
                            return pinnedLate;
                    }
                    if (cat.Discriminator.PieceOrCostItemIds is { Count: > 0 } pieceSet) {
                        var pinnedPiece = costs.Where(c => pieceSet.Contains(c.ItemId)).ToList();
                        if (pinnedPiece.Count > 0)
                            return pinnedPiece;
                    }
                    break;
                }
            }
        }
        return [.. costs];
    }

    internal string DisplayLabelForCategory(string categoryName) {
        lock (_glamourDataLock) {
            foreach (var c in _catalog.UITabsInOrder) {
                if (c.Name == categoryName)
                    return c.Name;
            }
        }
        return categoryName;
    }

    internal IReadOnlyList<GlamourSet> GetSharedModelSiblings(GlamourSet set) {
        lock (_glamourDataLock) {
            if (!_sharedModelGroups.TryGetValue(set.ModelSignature, out var group) || group.Count <= 1)
                return [];
            return [.. group.Where(s => s.ItemId != set.ItemId)];
        }
    }

    internal IReadOnlyList<uint> GetSharedModelItemSiblings(uint itemId) {
        lock (_glamourDataLock) {
            var row = Item.GetRow(itemId);
            var slot = row.EquipSlot;
            ItemModelInfo model = itemId;
            if (!_sharedModelItemGroups.TryGetValue(model, out var group) || group.Count <= 1)
                return [];
            return [.. group.Where(id => id != itemId && Item.GetRow(id).EquipSlot == slot)];
        }
    }

    internal GlamourSet? FindCatalogSetForItem(uint itemId) {
        lock (_glamourDataLock) {
            foreach (var set in GlamourSets) {
                if (set.NonSetCabinetPiece && set.ItemId == itemId)
                    return set;
            }
            return GlamourSets
                .Where(s => !s.NonSetCabinetPiece && s.Items.Contains(itemId))
                .OrderBy(s => s.ItemId)
                .FirstOrDefault();
        }
    }

    internal string GetCategoryBucketKey(GlamourSet set) {
        lock (_glamourDataLock)
            return _catalog.BucketKey(new ClassifyResult(set.CategoryName, set.IsUnobtainable));
    }

    private HashSet<uint> BuildAllPrimaryCostCurrencyIds() {
        var ids = new HashSet<uint>();
        foreach (var set in GlamourSets) {
            var cat = CategoryNameForPrimaryCostLookup(set);
            foreach (var pieceId in set.Items) {
                foreach (var c in GetPrimaryItemCosts(pieceId, cat)) {
                    if (c.ItemId != 0)
                        ids.Add(c.ItemId);
                }
            }
        }
        return ids;
    }

    private void LogMissingMirageSets() {
        try {
            var sourceRowIds = MirageStoreSetItem.Where(r => r.RowId > 0 && r.Items.Any(i => i.RowId > 0)).Select(r => r.RowId).ToHashSet();
            var classifiedRowIds = GlamourSets.Select(s => s.ItemId).ToHashSet();
            if (classifiedRowIds.Count >= sourceRowIds.Count)
                return;

            // TODO: dump full list not just some
            var missing = sourceRowIds.Where(id => !classifiedRowIds.Contains(id)).OrderBy(id => id).ToList();
            var preview = string.Join(", ", missing.Take(80));
            var suffix = missing.Count > 80 ? " ..." : string.Empty;
            Svc.Log.Warning($"[{nameof(CatalogService)}] Coverage gap: source={sourceRowIds.Count}, classified={classifiedRowIds.Count}, missing={missing.Count}. Missing MirageStoreSetItem rowIds: {preview}{suffix}");
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(CatalogService)}] mirage coverage diagnostics");
        }
    }
}
