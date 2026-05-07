using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog;

// TODO: different name for class
internal sealed unsafe class GlamourLogTracker : IDisposable {
    private bool _disposed;
    private LogWindow? _logWindow;
    private FilterWindow? _filterWindow;

    internal ReadOnlyCollection<GlamourSet> GlamourSets { get; private set; } = Array.Empty<GlamourSet>().ToList().AsReadOnly();
    internal Dictionary<string, List<GlamourSet>> GlamourSetsByCategory { get; } = [];
    internal ItemCostLookup CostsLookup { get; } = new();
    private readonly Lock _glamourDataLock = new();
    private readonly Lock _catalogRequestLock = new();
    private Catalog _catalog = Catalog.CreateEmptyStub();
    private volatile bool _catalogBuilt;
    private int _pendingListRefresh;
    private CancellationTokenSource? _catalogCts;
    private ArmoireService? _armoireService;
    internal HashSet<uint> ArmoireItemIds { get; private set; } = [];
    internal int DataVersion { get; private set; }

    internal void ToggleMainWindow() => _logWindow?.Toggle();

    public GlamourLogTracker() {
        Svc.ClientState.Login += OnClientLogin;
        _filterWindow = new FilterWindow(this) {
            InternalName = "GlamourLogFilter",
            Title = "Set list filters",
            Size = new Vector2(FilterWindow.WindowWidth, FilterWindow.WindowHeight),
            RememberClosePosition = false,
        };
        _logWindow = new LogWindow(this, _filterWindow) {
            InternalName = "GlamourLogMain",
            Title = "Glamour Sets",
            Size = new Vector2(920f, 640f),
        };
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
        DisposeArmoireService();
        _filterWindow?.Dispose();
        _filterWindow = null;
        _logWindow?.Dispose();
        _logWindow = null;
    }

    private void OnClientLogin() {
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

            var newArmoire = new ArmoireService();
            newArmoire.ArmoireChanged += NotifyDisplayedOwnershipMayHaveChanged;
            ArmoireService? oldArmoire = null;
            lock (_glamourDataLock) {
                token.ThrowIfCancellationRequested();
                _catalog = built.Catalog;
                ArmoireItemIds = built.ArmoireItemIds;
                GlamourSets = built.Sets;
                GlamourSetsByCategory.Clear();
                foreach (var group in GlamourSets.GroupBy(s => _catalog.BucketKey(new ClassifyResult(s.CategoryName, s.IsUnobtainable))))
                    GlamourSetsByCategory[group.Key] = [.. group];
                LogMissingMirageSets();
                oldArmoire = _armoireService;
                _armoireService = newArmoire;
                DataVersion++;
                _catalogBuilt = true;
            }
            if (oldArmoire is { } old) {
                old.ArmoireChanged -= NotifyDisplayedOwnershipMayHaveChanged;
                old.Dispose();
            }
            Interlocked.Exchange(ref _pendingListRefresh, 1);
        }
        catch (OperationCanceledException) {
            // cancelled by logout / disable / superseded build
        }
        catch (Exception ex) {
            Svc.PluginLog.Error(ex, $"{nameof(GlamourLogTracker)} catalog build");
        }
    }

    private void DisposeArmoireService() {
        if (_armoireService is not { } s)
            return;
        s.ArmoireChanged -= NotifyDisplayedOwnershipMayHaveChanged;
        s.Dispose();
        _armoireService = null;
    }

    internal bool CatalogReady => _catalogBuilt;

    internal bool TryConsumePendingListRefresh() => Interlocked.Exchange(ref _pendingListRefresh, 0) != 0;

    internal void MarkLogWindowDirty() => _logWindow?.RefreshListsAndDetails();

    /// <summary> Inventory / dresser changes that affect ownership or counts shown in the main log window. </summary>
    internal void NotifyDisplayedOwnershipMayHaveChanged() => _logWindow?.RefreshListsAndDetails();

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

    internal bool CanAffordAllMissingGearPieces(GlamourSet glamourSet, HashSet<uint> ownedItems) {
        (uint CostItemId, uint TotalAmount)? firstCost = null;
        uint totalCostQuantity = 0;
        foreach (var itemId in glamourSet.Items) {
            if (ownedItems.Contains(itemId)) continue;
            var costs = CostsLookup.GetItemCosts(itemId);
            if (costs.Count == 0) return false;
            var cost = costs[0];
            firstCost ??= (cost.ItemId, 0);
            if (firstCost.Value.CostItemId != cost.ItemId) return false;
            totalCostQuantity += cost.Amount;
        }
        if (firstCost == null) return false;
        var ownedCount = CurrencyManager.Instance()->SpecialItemBucket.TryGetValue(firstCost.Value.CostItemId, out var value, true)
            ? (int)value.Count
            : InventoryManager.Instance()->GetInventoryItemCount(firstCost.Value.CostItemId);
        return totalCostQuantity <= ownedCount;
    }

    internal bool IsPartiallyCompleted(GlamourSet glamourSet, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        if (ownedSets.Contains(glamourSet)) return false;
        var ownedCount = glamourSet.Items.Count(ownedItems.Contains);
        return ownedCount > 0 && ownedCount < glamourSet.Items.Count;
    }

    internal bool IsDoneButNotInDresser(GlamourSet glamourSet, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        if (ownedSets.Contains(glamourSet)) return false;
        var ownedCount = glamourSet.Items.Count(ownedItems.Contains);
        return ownedCount == glamourSet.Items.Count;
    }

    internal bool IsMarketboardPurchasable(GlamourSet glamourSet)
        => glamourSet.Items.Any(itemId => !Item.GetRow(itemId).IsUntradable);

    internal HashSet<uint> GetDresserStoredItemIds() {
        var dresserItemIds = new HashSet<uint>();
        if (ItemFinderModule.Instance() is null)
            return dresserItemIds;
        foreach (var dresserItemId in ItemFinderModule.Instance()->GlamourDresserBaseItemIds)
            dresserItemIds.Add(dresserItemId);
        return dresserItemIds;
    }

    internal HashSet<uint> GetArmoireOwnedItemIds() => _armoireService?.GetArmoireItems() ?? [];

    internal HashSet<uint> GetOwnedItems() {
        var dresserItemIds = GetDresserStoredItemIds();
        HashSet<uint> ownedItems = [.. dresserItemIds];

        foreach (var set in GlamourSets) {
            if (!dresserItemIds.Contains(set.ItemId))
                continue;

            foreach (var setItemId in set.Items)
                ownedItems.Add(setItemId);
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null) {
            foreach (var inventoryType in InventoryType.AllPlayer) {
                var inventoryContainer = inventoryManager->GetInventoryContainer(inventoryType);
                if (inventoryContainer == null) continue;
                for (var i = 0; i < inventoryContainer->Size; ++i) {
                    var item = inventoryContainer->GetInventorySlot(i);
                    if (item != null && item->ItemId != 0) ownedItems.Add(ItemUtil.GetBaseId(item->ItemId).ItemId);
                }
            }
        }
        ownedItems.UnionWith(GetArmoireOwnedItemIds());
        return ownedItems;
    }

    internal HashSet<GlamourSet> GetOwnedSets(HashSet<uint> ownedItems) => [.. GlamourSets.Where(set => GetDresserStoredItemIds().Contains(set.ItemId) || set.Items.All(ownedItems.Contains))];

    internal ItemStorageState GetItemStorageState(uint itemId, GlamourSet? forSet) {
        if (GetArmoireOwnedItemIds().Contains(itemId))
            return ItemStorageState.Armoire;

        var dresserItemIds = GetDresserStoredItemIds();
        if (forSet is not null && dresserItemIds.Contains(forSet.ItemId) && forSet.Items.Contains(itemId))
            return ItemStorageState.DresserSet;

        if (dresserItemIds.Contains(itemId))
            return ItemStorageState.DresserLoose;

        return ItemStorageState.None;
    }

    internal SetStorageState GetSetStorageState(GlamourSet set, HashSet<uint>? ownedItems = null) {
        var effectiveOwned = ownedItems ?? GetOwnedItems();
        if (!set.Items.All(effectiveOwned.Contains))
            return SetStorageState.None;

        var hasArmoire = false;
        var hasDresserSet = false;
        var hasDresserLoose = false;
        foreach (var itemId in set.Items) {
            switch (GetItemStorageState(itemId, set)) {
                case ItemStorageState.Armoire:
                    hasArmoire = true;
                    break;
                case ItemStorageState.DresserSet:
                    hasDresserSet = true;
                    break;
                case ItemStorageState.DresserLoose:
                    hasDresserLoose = true;
                    break;
            }
        }

        if (hasDresserLoose)
            return SetStorageState.None;
        if (hasArmoire && hasDresserSet)
            return SetStorageState.Mixed;
        if (hasArmoire)
            return SetStorageState.Armoire;
        if (hasDresserSet)
            return SetStorageState.Dresser;
        return SetStorageState.None;
    }

    /// <summary> Duty → chests → item ids for the Sources panel (gear in scope + <see cref="GetPrimaryItemCosts"/> currencies).</summary>
    internal IReadOnlyList<DutyChestSourceGroup> GetDutyChestSourceHierarchy(GlamourSet set, uint? costScopePieceItemId) {
        lock (_glamourDataLock) {
            var prov = _catalog.ChestLootProvenanceByItemId;
            var fateProv = _catalog.FateLootProvenanceByItemId;
            if (prov.Count == 0 && fateProv.Count == 0)
                return [];

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

            var byDuty = new Dictionary<uint, Dictionary<byte, HashSet<uint>>>();
            foreach (var itemId in itemIds) {
                if (!prov.TryGetValue(itemId, out var rows))
                    continue;
                foreach (var p in rows) {
                    if (p.ContentFinderConditionId == 0)
                        continue;
                    if (!byDuty.TryGetValue(p.ContentFinderConditionId, out var chestMap)) {
                        chestMap = [];
                        byDuty[p.ContentFinderConditionId] = chestMap;
                    }
                    var key = p.ChestNo;
                    if (!chestMap.TryGetValue(key, out var idSet)) {
                        idSet = [];
                        chestMap[key] = idSet;
                    }
                    idSet.Add(itemId);
                }
            }

            var byFate = new Dictionary<uint, HashSet<uint>>();
            foreach (var itemId in itemIds) {
                if (!fateProv.TryGetValue(itemId, out var fateRows))
                    continue;
                foreach (var p in fateRows) {
                    if (p.FateId == 0)
                        continue;
                    if (!byFate.TryGetValue(p.FateId, out var itemSet)) {
                        itemSet = [];
                        byFate[p.FateId] = itemSet;
                    }
                    itemSet.Add(itemId);
                }
            }

            var groups = new List<DutyChestSourceGroup>();
            foreach (var (cfcId, chestMap) in byDuty) {
                if (ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true, Value.NameFormatted.Length: > 0, Value.NameFormatted: var dutyName } cfc)
                    continue;

                var chestRows = new List<ChestSourceRow>();
                foreach (var kv in chestMap.OrderBy(x => x.Key)) {
                    var chestNo = kv.Key;
                    var sortedIds = kv.Value.OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
                    if (sortedIds.Count == 0)
                        continue;
                    chestRows.Add(new ChestSourceRow(chestNo, 0, sortedIds));
                }

                if (chestRows.Count == 0)
                    continue;

                groups.Add(new DutyChestSourceGroup(cfcId, dutyName, chestRows));
            }

            foreach (var (fateId, itemSet) in byFate) {
                var fateRow = Fate.GetRow(fateId);
                var fateName = fateRow.Name.ToString().Trim();
                if (fateName.Length == 0)
                    continue;
                var sortedIds = itemSet.OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
                if (sortedIds.Count == 0)
                    continue;
                groups.Add(new DutyChestSourceGroup(1_000_000u + fateId, fateName, [new ChestSourceRow(0, uint.MaxValue, sortedIds)]));
            }

            groups.Sort((a, b) => string.Compare(a.DutyName, b.DutyName, StringComparison.Ordinal));
            return groups;
        }
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

    // TODO: use inventorymanager/inventorytype extensions
    internal HashSet<uint> GetInventoryItemsOnly() {
        HashSet<uint> ownedItems = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager != null) {
            foreach (var inventoryType in InventoryType.AllPlayer) {
                var inventoryContainer = inventoryManager->GetInventoryContainer(inventoryType);
                if (inventoryContainer == null) continue;
                for (var i = 0; i < inventoryContainer->Size; ++i) {
                    var item = inventoryContainer->GetInventorySlot(i);
                    if (item != null && item->ItemId != 0) ownedItems.Add(ItemUtil.GetBaseId(item->ItemId).ItemId);
                }
            }
        }
        return ownedItems;
    }

    // TODO: make ContainsAny extensions that take predicates
    internal bool IsItemInGlamourDresser(uint itemId, GlamourSet? forSet) {
        if (ItemFinderModule.Instance() is null)
            return false;
        foreach (var id in ItemFinderModule.Instance()->GlamourDresserBaseItemIds) {
            if (id == itemId)
                return true;
            if (forSet != null && id == forSet.ItemId)
                return true;
        }
        return false;
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
            Svc.PluginLog.Warning($"[{nameof(GlamourLogTracker)}] Coverage gap: source={sourceRowIds.Count}, classified={classifiedRowIds.Count}, missing={missing.Count}. Missing MirageStoreSetItem rowIds: {preview}{suffix}");
        }
        catch (Exception ex) {
            Svc.PluginLog.Error(ex, $"[{nameof(GlamourLogTracker)}] mirage coverage diagnostics");
        }
    }
}
