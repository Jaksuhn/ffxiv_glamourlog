using AllaganLib.GameSheets.Caches;
using AllaganLib.GameSheets.Extensions;
using AllaganLib.GameSheets.ItemSources;
using AllaganLib.GameSheets.Model;
using AllaganLib.GameSheets.Sheets.Rows;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog.Services;

internal sealed unsafe class CatalogService : IDisposable {
    private bool _disposed;

    internal ReadOnlyCollection<GlamourSet> GlamourSets { get; private set; } = Array.Empty<GlamourSet>().ToList().AsReadOnly();
    internal Dictionary<string, List<GlamourSet>> GlamourSetsByCategory { get; } = [];
    internal ItemCostLookup CostsLookup { get; } = new();
    internal HashSet<uint> ArmoireItemIds { get; private set; } = [];

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
        lock (_glamourDataLock)
            _catalogBuilt = false;
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
                LogMissingMirageSets();
                DataVersion++;
                _catalogBuilt = true;
            }
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

    internal bool TryConsumePendingListRefresh() => Interlocked.Exchange(ref _pendingListRefresh, 0) != 0;

    internal void MarkLogWindowDirty() => Svc.Get<WindowsService>().RefreshLogWindow();

    /// <summary> Inventory / dresser changes that affect ownership or counts shown in the main log window. </summary>
    internal void NotifyDisplayedOwnershipMayHaveChanged() => Svc.Get<WindowsService>().RefreshLogWindow();

    /// <summary> Real outfit tabs (excludes synthetic uncategorized / unobtainable rows).</summary>
    internal IReadOnlyList<OutfitCategory> OutfitCategories => _catalog.ClassifiableCategories;
    internal OutfitCategory UncategorizedTab => _catalog.UncategorizedBucket;
    internal OutfitCategory UnobtainableTab => _catalog.UnobtainableBucket;

    internal string? CategoryNameForPrimaryCostLookup(GlamourSet set) {
        if (set.IsUnobtainable || set.CategoryName is null)
            return null;
        lock (_glamourDataLock) {
            if (set.CategoryName == _catalog.UncategorizedBucket.Name || set.CategoryName == _catalog.UnobtainableBucket.Name)
                return null;
            return set.CategoryName;
        }
    }

    /// <summary> Category for <see cref="GetPrimaryItemCosts"/> when browsing a bucket tab (not a <see cref="GlamourSet"/>).</summary>
    internal string? CategoryNameForCostDiscriminatorBucket(string categoryName) {
        lock (_glamourDataLock) {
            if (categoryName == _catalog.UncategorizedBucket.Name || categoryName == _catalog.UnobtainableBucket.Name)
                return null;
            return categoryName;
        }
    }

    /// <summary> Source groups for the Sources panel (duty/fate/vendor) based on AllaganLib item sources for gear in scope + <see cref="GetPrimaryItemCosts"/> currencies.</summary>
    internal IReadOnlyList<SourceGroup> GetSourceHierarchy(GlamourSet set, uint? costScopePieceItemId) {
        lock (_glamourDataLock) {
            var itemIds = BuildSourceScopeItemIds(set, costScopePieceItemId);
            if (itemIds.Count == 0)
                return [];

            var cache = Svc.SheetManager.ItemInfoCache;
            var dutyItems = new Dictionary<uint, HashSet<uint>>();
            var fateItems = new Dictionary<uint, HashSet<uint>>();
            var npcVendors = new Dictionary<uint, NpcVendorAccumulator>();
            var orphanShops = new Dictionary<(ItemInfoType Type, uint ShopId), OrphanShopAccumulator>();

            foreach (var itemId in itemIds) {
                var sources = cache.GetItemSources(itemId);
                if (sources is not { Count: > 0 })
                    continue;
                foreach (var source in sources) {
                    switch (source) {
                        case ItemDungeonSource duty when duty.ContentFinderCondition.RowId != 0:
                            if (!dutyItems.TryGetValue(duty.ContentFinderCondition.RowId, out var dutySet)) {
                                dutySet = [];
                                dutyItems[duty.ContentFinderCondition.RowId] = dutySet;
                            }
                            dutySet.Add(itemId);
                            break;
                        case ItemFateSource fate when fate.Fate.RowId != 0:
                            if (!fateItems.TryGetValue(fate.Fate.RowId, out var fateSet)) {
                                fateSet = [];
                                fateItems[fate.Fate.RowId] = fateSet;
                            }
                            fateSet.Add(itemId);
                            break;
                        case ItemShopSource shopSource when source.Type.IsShop():
                            AddShopSourcesForItem(shopSource.Shop, source.Type, itemId, npcVendors, orphanShops);
                            break;
                        case ItemCashShopSource:
                            var cKey = (ItemInfoType.CashShop, 0u);
                            if (!orphanShops.TryGetValue(cKey, out var cashAcc)) {
                                cashAcc = new OrphanShopAccumulator(
                                    "Mog Station",
                                    FormatShopTypeLabel(ItemInfoType.CashShop));
                                orphanShops[cKey] = cashAcc;
                            }
                            cashAcc.ItemIds.Add(itemId);
                            break;
                    }
                }
            }

            var groups = new List<SourceGroup>();
            foreach (var (cfcId, setItems) in dutyItems) {
                if (ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true, Value.NameFormatted.Length: > 0, Value.NameFormatted: var dutyName })
                    continue;
                var sortedIds = setItems.OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
                if (sortedIds.Count == 0)
                    continue;
                groups.Add(new SourceGroup(
                    SourceGroupKind.Duty,
                    cfcId,
                    dutyName,
                    [new SourceRow("Duty Loot", string.Empty, sortedIds)]));
            }

            foreach (var (fateId, setItems) in fateItems) {
                var fateName = Fate.GetRow(fateId).Name.ToString().Trim();
                if (fateName.Length == 0)
                    continue;
                var sortedIds = setItems.OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
                if (sortedIds.Count == 0)
                    continue;
                groups.Add(new SourceGroup(
                    SourceGroupKind.Fate,
                    0,
                    fateName,
                    [new SourceRow("FATE Reward", string.Empty, sortedIds)]));
            }

            foreach (var acc in npcVendors.Values.OrderBy(a => a.Name, StringComparer.Ordinal)) {
                var rows = new List<SourceRow>();
                foreach (var kv in acc.Shops.OrderBy(e => e.Value.ShopName, StringComparer.Ordinal)) {
                    var entry = kv.Value;
                    var sortedIds = entry.ItemIds.OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
                    if (sortedIds.Count == 0)
                        continue;
                    rows.Add(new SourceRow(entry.ShopName, acc.NavigationText, sortedIds));
                }
                if (rows.Count == 0)
                    continue;
                groups.Add(new SourceGroup(SourceGroupKind.Vendor, 0, acc.Name, rows));
            }

            foreach (var acc in orphanShops.Values.OrderBy(a => a.HeaderName, StringComparer.Ordinal)) {
                var sortedIds = acc.ItemIds.OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
                if (sortedIds.Count == 0)
                    continue;
                groups.Add(new SourceGroup(
                    SourceGroupKind.Vendor,
                    0,
                    acc.HeaderName,
                    [new SourceRow(acc.ShopLineLabel, acc.NavigationHint, sortedIds)]));
            }

            groups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return groups;
        }
    }

    private static void AddShopSourcesForItem(
        IShop shop,
        ItemInfoType shopType,
        uint itemId,
        Dictionary<uint, NpcVendorAccumulator> npcVendors,
        Dictionary<(ItemInfoType Type, uint ShopId), OrphanShopAccumulator> orphanShops) {
        var shopKey = (shopType, shop.RowId);
        var shopDisplayName = shop.Name.Trim();
        if (string.IsNullOrEmpty(shopDisplayName))
            shopDisplayName = FormatShopTypeLabel(shopType);

        var npcList = shop.ENpcs.OfType<ENpcBaseRow>().Where(n => n.RowId != 0).ToList();
        if (npcList.Count == 0) {
            if (!orphanShops.TryGetValue(shopKey, out var orphan)) {
                orphan = new OrphanShopAccumulator(shopDisplayName, shopDisplayName);
                orphanShops[shopKey] = orphan;
            }
            orphan.ItemIds.Add(itemId);
            return;
        }

        foreach (var npc in npcList) {
            if (!npcVendors.TryGetValue(npc.RowId, out var acc))
                acc = npcVendors[npc.RowId] = new NpcVendorAccumulator(npc);
            if (!acc.Shops.TryGetValue(shopKey, out var entry)) {
                entry = new ShopEntry(shopDisplayName);
                acc.Shops[shopKey] = entry;
            }
            entry.ItemIds.Add(itemId);
        }
    }

    private static string FormatNpcNavigation(ENpcBaseRow npc) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var loc in npc.Locations) {
            if (loc is not NpcLocation n)
                continue;
            var mx = (int)Math.Round(n.MapX);
            var my = (int)Math.Round(n.MapY);
            var line = $"{n.TerritoryType.Value.PlaceName.Value.Name} ({mx}, {my})";
            if (seen.Add(line))
                parts.Add(line);
        }
        return parts.Count == 0 ? "Location unknown" : string.Join("; ", parts);
    }

    private sealed class NpcVendorAccumulator {
        internal NpcVendorAccumulator(ENpcBaseRow npc) {
            Npc = npc;
            Name = npc.Name.Trim();
            if (Name.Length == 0)
                Name = $"NPC #{npc.RowId}";
        }

        internal ENpcBaseRow Npc { get; }
        internal string Name { get; }
        internal Dictionary<(ItemInfoType Type, uint ShopId), ShopEntry> Shops { get; } = [];
        private string? _navigationText;
        internal string NavigationText => _navigationText ??= FormatNpcNavigation(Npc);
    }

    private sealed class ShopEntry(string shopName) {
        internal string ShopName { get; } = shopName;
        internal HashSet<uint> ItemIds { get; } = [];
    }

    private sealed class OrphanShopAccumulator {
        internal OrphanShopAccumulator(string headerName, string shopRowLabel) {
            HeaderName = headerName;
            ShopLineLabel = shopRowLabel;
        }

        internal string HeaderName { get; }
        /// <summary>Shown on the row with item icons (typically the shop name).</summary>
        internal string ShopLineLabel { get; }
        internal string NavigationHint => "Location unknown";
        internal HashSet<uint> ItemIds { get; } = [];
    }

    internal IReadOnlyList<ItemInfoType> GetSourceTypesForSet(GlamourSet set, uint? costScopePieceItemId) {
        lock (_glamourDataLock) {
            var itemIds = BuildSourceScopeItemIds(set, costScopePieceItemId);
            if (itemIds.Count == 0)
                return [];

            var cache = Svc.SheetManager.ItemInfoCache;
            var typeSet = new HashSet<ItemInfoType>();
            foreach (var itemId in itemIds) {
                var sources = cache.GetItemSources(itemId);
                if (sources is not { Count: > 0 })
                    continue;
                foreach (var source in sources)
                    typeSet.Add(source.Type);
            }
            return [.. typeSet.OrderBy(t => t.ToString(), StringComparer.Ordinal)];
        }
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

    private static string FormatShopTypeLabel(ItemInfoType type)
        => type.ToString().Replace("Shop", " Shop", StringComparison.Ordinal);

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
