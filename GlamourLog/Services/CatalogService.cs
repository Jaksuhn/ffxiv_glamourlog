using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog.Services;

internal sealed class CatalogService : IDisposable {
    private bool _disposed;

    internal ReadOnlyCollection<GlamourSet> GlamourSets { get; private set; } = new ReadOnlyCollection<GlamourSet>([]);
    internal Dictionary<string, List<GlamourSet>> GlamourSetsByCategory { get; } = [];
    internal ItemCostLookup CostsLookup { get; } = new();
    internal HashSet<uint> ArmoireItemIds { get; private set; } = [];
    internal HashSet<uint> MirageOutfitPieceIds { get; private set; } = [];
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

        if (Svc.ClientState.IsLoggedIn)
            InvalidateCatalog();
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

    private void OnClientLogin() => InvalidateCatalog();
    internal void OnArmoireChanged() => InvalidateCatalog();

    private void InvalidateCatalog() {
        lock (_glamourDataLock)
            _catalogBuilt = false;
        RequestCatalogBuild();
    }

    private unsafe void RequestCatalogBuild() {
        if (_catalogBuilt)
            return;
        lock (_catalogRequestLock) {
            if (_catalogBuilt)
                return;
            _catalogCts?.Cancel();
            _catalogCts?.Dispose();
            _catalogCts = new CancellationTokenSource();
            var token = _catalogCts.Token;
            Svc.Framework.RunOnFrameworkThread(() => {
                if (token.IsCancellationRequested)
                    return;
                var pvpSeries = PvPProfile.Instance() is not null and var pvp ? pvp->Series : (byte)0;
                uint[] tradecraftIds = CurrencyManager.Instance() is not null and var cm ? [.. new byte[] { 1, 2, 3, 4, 6, 7 }.Select(sid => cm->GetItemIdBySpecialId(sid)).Where(id => id != 0).Distinct()] : [];
                _ = Task.Run(() => RunCatalogBuild(pvpSeries, tradecraftIds, token), token);
            });
        }
    }

    private void RunCatalogBuild(byte pvpSeries, uint[] tradecraftIds, CancellationToken token) {
        try {
            token.ThrowIfCancellationRequested();
            var built = CatalogBuilder.Run(CostsLookup, pvpSeries, tradecraftIds);
            token.ThrowIfCancellationRequested();

            lock (_glamourDataLock) {
                token.ThrowIfCancellationRequested();
                _catalog = built.Catalog;
                ArmoireItemIds = built.ArmoireItemIds;
                GlamourSets = built.Sets;
                MirageOutfitPieceIds = [.. GlamourSets.Where(s => !s.NonSetCabinetPiece).SelectMany(s => s.Items)];
                GlamourSetsByCategory.Clear();
                foreach (var group in GlamourSets.GroupBy(s => _catalog.BucketKey(new ClassifyResult(s.CategoryName, s.IsUnobtainable))))
                    GlamourSetsByCategory[group.Key] = [.. group];
                _sharedModelGroups = GlamourSets.GroupBy(s => s.ModelSignature).ToDictionary(g => g.Key, g => g.OrderBy(s => s.ItemId).ToList());
                _sharedModelItemGroups = CatalogBuilder.BuildSharedModelItemGroups(GlamourSets.SelectMany(s => s.Items));
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
    internal bool IsMirageOutfitPiece(uint itemId) => itemId != 0 && _catalogBuilt && MirageOutfitPieceIds.Contains(itemId);
    internal bool TryConsumePendingListRefresh() => Interlocked.Exchange(ref _pendingListRefresh, 0) != 0;
    internal void NotifyOwnershipChanged() => Svc.Get<WindowsService>().RefreshLogWindow();

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
        var pieces = costScopePieceItemId is { } only ? (IEnumerable<uint>)[only] : set.Items;
        foreach (var pieceId in pieces) {
            itemIds.Add(pieceId);
            foreach (var c in GetPrimaryItemCosts(pieceId, cat))
                if (c.ItemId != 0)
                    itemIds.Add(c.ItemId);
        }
        return itemIds;
    }

    internal List<(uint ItemId, uint Amount)> GetPrimaryItemCosts(uint itemId, string? categoryNameForDiscriminator) {
        var costs = CostsLookup.GetItemCosts(itemId);
        if (costs.Count == 0)
            return [];
        if (!string.IsNullOrEmpty(categoryNameForDiscriminator)) {
            lock (_glamourDataLock) {
                var cat = _catalog.ClassifiableCategories.FirstOrDefault(c => c.Name == categoryNameForDiscriminator);
                if (cat is not null) {
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
                }
            }
        }
        return [.. costs];
    }

    internal IReadOnlyList<GlamourSet> GetSharedModelSiblings(GlamourSet set) {
        lock (_glamourDataLock) {
            if (!_sharedModelGroups.TryGetValue(set.ModelSignature, out var group) || group.Count <= 1)
                return [];
            return [.. group.Where(s => s.ItemId != set.ItemId)];
        }
    }

    internal IReadOnlyList<uint> GetSharedModelItemSiblings(uint itemId) {
        lock (_glamourDataLock)
            return GetSharedModelItemSiblingsUnlocked(itemId, _sharedModelItemGroups);
    }

    internal IReadOnlyList<GlamourSet> GetPartialSharedModelSetSiblings(GlamourSet set) {
        lock (_glamourDataLock) {
            var results = new List<GlamourSet>();
            var seen = new HashSet<GlamourSet>();
            foreach (var pieceId in set.Items) {
                foreach (var siblingId in GetSharedModelItemSiblingsUnlocked(pieceId, _sharedModelItemGroups)) {
                    var siblingSet = FindCatalogSetForItemUnlocked(siblingId);
                    if (siblingSet is null || ReferenceEquals(siblingSet, set) || !seen.Add(siblingSet))
                        continue;
                    results.Add(siblingSet);
                }
            }
            return results;
        }
    }

    private static IReadOnlyList<uint> GetSharedModelItemSiblingsUnlocked(uint itemId, Dictionary<ItemModelInfo, List<uint>> itemGroups) {
        var row = Item.GetRow(itemId);
        var slot = row.EquipSlot;
        ItemModelInfo model = itemId;
        if (!itemGroups.TryGetValue(model, out var group) || group.Count <= 1)
            return [];
        return [.. group.Where(id => id != itemId && Item.GetRow(id).EquipSlot == slot)];
    }

    internal GlamourSet? FindCatalogSetForItem(uint itemId) {
        lock (_glamourDataLock)
            return FindCatalogSetForItemUnlocked(itemId);
    }

    private GlamourSet? FindCatalogSetForItemUnlocked(uint itemId) {
        foreach (var set in GlamourSets) {
            if (set.NonSetCabinetPiece && set.ItemId == itemId)
                return set;
        }
        return GlamourSets
            .Where(s => !s.NonSetCabinetPiece && s.Items.Contains(itemId))
            .OrderBy(s => s.ItemId)
            .FirstOrDefault();
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
