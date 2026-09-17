using AllaganLib.GameSheets.Caches;
using AllaganLib.GameSheets.Extensions;
using AllaganLib.GameSheets.ItemSources;
using AllaganLib.GameSheets.Sheets.Rows;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog.Services;

internal sealed class CatalogService : IPluginService, IDisposable {
    private static readonly HashSet<ItemInfoType> LootboxSourceTypes = [
        ItemInfoType.Anemos,
        ItemInfoType.Pagos,
        ItemInfoType.Pyros,
        ItemInfoType.Hydatos,
        ItemInfoType.Bozja,
        ItemInfoType.OccultTreasure,
        ItemInfoType.PalaceOfTheDead,
        ItemInfoType.HeavenOnHigh,
        ItemInfoType.EurekaOrthos,
        ItemInfoType.Coffer,
        ItemInfoType.Loot,
        ItemInfoType.PagosTreasure,
        ItemInfoType.PyrosTreasure,
        ItemInfoType.HydatosTreasure,
        ItemInfoType.OccultPot,
        ItemInfoType.OccultGoldenCoffer,
        ItemInfoType.Logogram,
        ItemInfoType.PilgrimsTraverse,
        ItemInfoType.Oizys,
    ];

    internal ReadOnlyCollection<GlamourSet> GlamourSets { get; private set; } = new ReadOnlyCollection<GlamourSet>([]);
    internal Dictionary<string, List<GlamourSet>> GlamourSetsByCategory { get; } = [];
    internal HashSet<uint> ArmoireItemIds { get; private set; } = [];
    internal HashSet<uint> MirageOutfitPieceIds { get; private set; } = [];
    internal HashSet<uint> MirageSetTokenIds { get; private set; } = [];
    private HashSet<uint> _costCurrencyItemIds = [];
    private IReadOnlyList<uint> _currencyFilterAll = [];
    private Dictionary<string, IReadOnlyList<uint>> _currencyFilterByDisplayCategory = [];
    private Dictionary<uint, HashSet<uint>> _setFilterCurrencyIdsBySetId = [];
    private SourceFilterIndex _sourceFilterIndex = SourceFilterIndex.Empty;
    private Dictionary<SetModelSignature, List<GlamourSet>> _sharedModelGroups = [];
    private Dictionary<ItemModelInfo, List<uint>> _sharedModelItemGroups = [];

    private readonly Lock _glamourDataLock = new();
    private readonly Lock _catalogRequestLock = new();
    private Catalog _catalog = Catalog.CreateEmptyStub();
    private volatile bool _catalogBuilt;
    private int _pendingListRefresh; // ui picks this up next frame after a background rebuild
    private CancellationTokenSource? _catalogCts;

    private readonly UnobtainableService _unobtainable;

    internal int DataVersion { get; private set; }

    public CatalogService() {
        _unobtainable = UnobtainableService.Get();
        IClientState.Get().Login += OnClientLogin;
        _unobtainable.Changed += OnUnobtainableChanged;

        if (IClientState.Get().IsLoggedIn)
            InvalidateCatalog();
    }

    public void Dispose() {
        IClientState.Get().Login -= OnClientLogin;
        _unobtainable.Changed -= OnUnobtainableChanged;
        _catalogCts?.Cancel();
        _catalogCts?.Dispose();
        _catalogCts = null;
        lock (_glamourDataLock) {
            _catalogBuilt = false;
            _costCurrencyItemIds = [];
            _currencyFilterAll = [];
            _currencyFilterByDisplayCategory = [];
            _setFilterCurrencyIdsBySetId = [];
            _sourceFilterIndex = SourceFilterIndex.Empty;
        }
    }

    private void OnClientLogin() => InvalidateCatalog();
    private void OnUnobtainableChanged() {
        if (_catalogBuilt)
            RefreshUnobtainable();
    }
    internal void OnArmoireChanged() => InvalidateCatalog();

    private void InvalidateCatalog() {
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
            _ = Task.Run(async () => {
                static unsafe bool CurrencyManagerReady() => CurrencyManager.Instance() != null; // unnecessary unsafe modifier my fucking ass, microslop
                // crafting currency ids aren't available until this shows up after login
                while (!CurrencyManagerReady())
                    await Task.Delay(1000, token);
                RunCatalogBuild(token);
            }, token);
        }
    }

    private unsafe void RunCatalogBuild(CancellationToken token) {
        try {
            token.ThrowIfCancellationRequested();
            var pvpSeries = PvPProfile.Instance()->Series;
            var built = CatalogBuilder.Run();
            token.ThrowIfCancellationRequested();

            lock (_glamourDataLock) {
                token.ThrowIfCancellationRequested();
                _catalog = built.Catalog;
                ArmoireItemIds = built.ArmoireItemIds;
                GlamourSets = built.Sets;
                MirageOutfitPieceIds = [.. GlamourSets.Where(s => !s.NonSetCabinetPiece).SelectMany(s => s.Items)];
                MirageSetTokenIds = [.. GlamourSets.Where(s => !s.NonSetCabinetPiece).Select(s => s.ItemId)];
                RebuildCategoryMapUnlocked();
                _sharedModelGroups = GlamourSets.GroupBy(s => s.ModelSignature).ToDictionary(g => g.Key, g => g.OrderBy(s => s.ItemId).ToList());
                _sharedModelItemGroups = CatalogBuilder.BuildSharedModelItemGroups(GlamourSets.SelectMany(s => s.Items));
                LogMissingMirageSets();
                DataVersion++;
                _catalogBuilt = true;
            }
            RebuildCostCurrencyIndexes();
            RebuildSourceFilterIndexes();
            Interlocked.Exchange(ref _pendingListRefresh, 1);
            WindowsService.Get().RefreshLogWindow();
        }
        catch (OperationCanceledException) {
            // cancelled by logout / disable / superseded build
        }
        catch (Exception ex) {
            IPluginLog.Get().Error(ex, $"{nameof(CatalogService)} catalog build");
        }
    }

    private void RefreshUnobtainable() {
        lock (_glamourDataLock) {
            if (!_catalogBuilt)
                return;
            GlamourSets = CatalogBuilder.ReapplyUnobtainable(GlamourSets);
            RebuildCategoryMapUnlocked();
            _sharedModelGroups = GlamourSets.GroupBy(s => s.ModelSignature).ToDictionary(g => g.Key, g => g.OrderBy(s => s.ItemId).ToList());
            DataVersion++;
        }
        Interlocked.Exchange(ref _pendingListRefresh, 1);
        WindowsService.Get().RefreshLogWindow();
    }

    private void RebuildCategoryMapUnlocked() {
        GlamourSetsByCategory.Clear();
        foreach (var group in GlamourSets.GroupBy(s => _catalog.GetDisplayCategoryName(s.CategoryName)))
            GlamourSetsByCategory[group.Key] = [.. group];
    }

    internal bool CatalogReady => _catalogBuilt;

    internal bool IsKnownCostCurrency(uint itemId) => itemId != 0 && _catalogBuilt && _costCurrencyItemIds.Contains(itemId);

    // currencies w/ cost > 1, null = all
    internal IReadOnlyList<uint> GetCurrencyFilterItemIds(string? displayCategoryName) {
        if (!_catalogBuilt)
            return [];
        if (displayCategoryName is null)
            return _currencyFilterAll;
        return _currencyFilterByDisplayCategory.TryGetValue(displayCategoryName, out var list) ? list : [];
    }

    internal bool SetUsesCurrencyFilter(GlamourSet set, uint currencyItemId) => currencyItemId != 0 && _setFilterCurrencyIdsBySetId.TryGetValue(set.ItemId, out var ids) && ids.Contains(currencyItemId);
    internal bool SetMatchesSourceFilter(GlamourSet set, ItemInfoType source, string subSourceKey) {
        var index = _sourceFilterIndex;
        if (!index.BySetId.TryGetValue(set.ItemId, out var bySource) || !bySource.TryGetValue(source, out var keys))
            return false;
        var validSubSource = !string.IsNullOrEmpty(subSourceKey) && index.Options.TryGetValue(source, out var options) && options.Any(option => option.Key == subSourceKey);
        return !validSubSource || keys.Contains(subSourceKey);
    }

    internal IReadOnlyList<SourceFilterOption> GetSourceFilterOptions()
        => [.. _sourceFilterIndex.SourceTypes.Select(type => new SourceFilterOption(type, ToName(type)))];

    internal IReadOnlyList<SourceSubFilterOption> GetSubSourceFilterOptions(ItemInfoType source)
        => _sourceFilterIndex.Options.GetValueOrDefault(source) ?? [];
    internal bool IsMirageOutfitPiece(uint itemId) => itemId != 0 && _catalogBuilt && MirageOutfitPieceIds.Contains(itemId);
    internal bool TryConsumePendingListRefresh() => Interlocked.Exchange(ref _pendingListRefresh, 0) != 0;
    internal void NotifyOwnershipChanged() => WindowsService.Get().RefreshLogWindow();

    internal IReadOnlyList<OutfitCategory> OutfitCategories => _catalog.ClassifiableCategories; // excludes synthetic (non rule-matched) categories
    internal OutfitCategory UncategorizedTab => _catalog.UncategorizedBucket;
    internal OutfitCategory MiscArmoireTab => _catalog.MiscArmoireBucket;

    // synthetic tabs don't have a preferred currency to pin costs to
    internal string? GetCategoryForPreferredCost(GlamourSet set) {
        if (set.CategoryName is null)
            return null;
        lock (_glamourDataLock) {
            return set.CategoryName == _catalog.UncategorizedBucket.Name || set.CategoryName == _catalog.MiscArmoireBucket.Name || set.NonSetCabinetPiece ? null : set.CategoryName;
        }
    }

    // item ids the sources panel should look up (set pieces + their main cost currencies for the current filter)
    internal IReadOnlyCollection<uint> GetSourceScopeItemIds(GlamourSet set, uint? filterPieceId) {
        lock (_glamourDataLock)
            return [.. BuildSourceScopeItemIds(set, filterPieceId)];
    }

    private HashSet<uint> BuildSourceScopeItemIds(GlamourSet set, uint? filterPieceId) {
        var cat = GetCategoryForPreferredCost(set);
        var itemIds = new HashSet<uint>();
        var pieces = filterPieceId is { } only ? (IEnumerable<uint>)[only] : set.Items;
        foreach (var pieceId in pieces) {
            itemIds.Add(pieceId);
            foreach (var c in GetPrimaryItemCosts(pieceId, cat))
                if (c.ItemId != 0)
                    itemIds.Add(c.ItemId);
        }
        return itemIds;
    }

    // prefer the currency that put this set in its current tab (falls back to all listed costs)
    internal List<(uint ItemId, uint Amount)> GetPrimaryItemCosts(uint itemId, string? categoryNameForDiscriminator) {
        var costs = Svc.Items.GetItemCosts(itemId);
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

    // prefer a standalone armoire entry over the first matching outfit that contains the piece
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
            return _catalog.GetDisplayCategoryName(set.CategoryName);
    }

    private void RebuildCostCurrencyIndexes() {
        var allKnown = new HashSet<uint>();
        var allFilter = new HashSet<uint>();
        var byDisplay = new Dictionary<string, HashSet<uint>>();
        var bySetId = new Dictionary<uint, HashSet<uint>>();

        foreach (var set in GlamourSets) {
            var preferred = GetCategoryForPreferredCost(set);
            var display = GetCategoryBucketKey(set);
            var setFilterIds = new HashSet<uint>();
            foreach (var pieceId in set.Items) {
                foreach (var (costId, amount) in GetPrimaryItemCosts(pieceId, preferred)) {
                    if (costId == 0)
                        continue;
                    allKnown.Add(costId);
                    if (amount <= 1)
                        continue;
                    setFilterIds.Add(costId);
                    allFilter.Add(costId);
                    if (!byDisplay.TryGetValue(display, out var catIds))
                        byDisplay[display] = catIds = [];
                    catIds.Add(costId);
                }
            }
            if (setFilterIds.Count > 0)
                bySetId[set.ItemId] = setFilterIds;
        }

        static List<uint> SortedByName(HashSet<uint> ids)
            => [.. ids.OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal)];

        _costCurrencyItemIds = allKnown;
        _currencyFilterAll = SortedByName(allFilter);
        _currencyFilterByDisplayCategory = byDisplay.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<uint>)SortedByName(kv.Value));
        _setFilterCurrencyIdsBySetId = bySetId;
    }

    private void RebuildSourceFilterIndexes() {
        var bySetId = new Dictionary<uint, Dictionary<ItemInfoType, HashSet<string>>>();
        var optionLabels = new Dictionary<ItemInfoType, Dictionary<string, string>>();
        var acquisition = ItemAcquisitionService.Get();

        foreach (var set in GlamourSets) {
            var bySource = new Dictionary<ItemInfoType, HashSet<string>>();
            foreach (var itemId in BuildSourceScopeItemIds(set, filterPieceId: null)) {
                foreach (var source in acquisition.GetSources(itemId))
                    AddSource(source, bySource, optionLabels);
            }
            if (bySource.Count > 0)
                bySetId[set.ItemId] = bySource;
        }

        var options = optionLabels.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<SourceSubFilterOption>)[.. entry.Value
                .Select(option => new SourceSubFilterOption(option.Key, option.Value))
                .OrderBy(option => entry.Key == ItemInfoType.PVPSeries ? NumericKeySuffix(option.Key) : 0U)
                .ThenBy(option => option.Label, StringComparer.Ordinal)
                .ThenBy(option => option.Key, StringComparer.Ordinal)]);

        var sourceTypes = bySetId.Values
            .SelectMany(bySource => bySource.Keys)
            .Distinct()
            .OrderBy(ToName, StringComparer.Ordinal)
            .ToList();
        _sourceFilterIndex = new SourceFilterIndex(bySetId, options, sourceTypes);
    }

    private static void AddSource(ItemSource source, Dictionary<ItemInfoType, HashSet<string>> bySource, Dictionary<ItemInfoType, Dictionary<string, string>> optionLabels) {
        switch (source) {
            case ItemShopSource shopSource when shopSource.Type.IsShop():
                AddVendorSource(shopSource, bySource, optionLabels);
                return;
            case ItemDungeonSource dutySource:
                AddSourceOption(source.Type, $"duty:{dutySource.ContentFinderCondition.RowId}", DutyName(dutySource.ContentFinderCondition.RowId), bySource, optionLabels);
                return;
            case ItemQuestSource questSource:
                AddSourceOption(source.Type, $"quest:{questSource.Quest.RowId}", questSource.Quest.RowId == 0 ? string.Empty : questSource.Quest.Value.Name.ToString(), bySource, optionLabels);
                return;
            case ItemCraftResultSource craftSource:
                AddSourceOption(source.Type, $"recipe:{craftSource.Recipe.RowId}", ItemName(craftSource.Item.RowId), bySource, optionLabels);
                return;
            case ItemFateSource fateSource:
                AddSourceOption(source.Type, $"fate:{fateSource.Fate.RowId}", fateSource.Fate.RowId == 0 ? string.Empty : Fate.GetRow(fateSource.Fate.RowId).Name.ToString(), bySource, optionLabels);
                return;
            case ItemDesynthSource desynthSource:
                AddSourceOption(source.Type, $"item:{desynthSource.CostItem?.RowId ?? 0}", ItemName(desynthSource.CostItem?.RowId ?? 0), bySource, optionLabels);
                return;
            case ItemAchievementSource achievementSource:
                AddSourceOption(source.Type, $"achievement:{achievementSource.Achievement.RowId}", achievementSource.Achievement.RowId == 0 ? string.Empty : achievementSource.Achievement.Value.Name.ToString(), bySource, optionLabels);
                return;
            case ItemPVPSeriesSource seriesSource:
                AddSourceOption(source.Type, $"pvp-series:{seriesSource.PvpSeries.RowId}", seriesSource.PvpSeries.RowId == 0 ? string.Empty : $"Series {seriesSource.PvpSeries.RowId}", bySource, optionLabels);
                return;
            case ItemCashShopSource:
                EnsureSource(source.Type, bySource);
                return;
            case ItemSupplementSource supplementSource when LootboxSourceTypes.Contains(supplementSource.Type):
                var costItemId = supplementSource.CostItem?.RowId ?? 0;
                AddSourceOption(
                    source.Type,
                    costItemId != 0 ? $"item:{costItemId}" : $"source-type:{(uint)supplementSource.Type}",
                    costItemId != 0 ? ItemName(costItemId) : ToName(supplementSource.Type),
                    bySource,
                    optionLabels);
                return;
            case ItemFieldOpCofferSource fieldSource:
                AddSourceOption(source.Type, $"field-coffer:{(uint)fieldSource.Type}:{(uint)fieldSource.CofferType}", $"{ToName(fieldSource.Type)} ({fieldSource.CofferType})", bySource, optionLabels);
                return;
            default:
                EnsureSource(source.Type, bySource);
                return;
        }
    }

    private static void AddVendorSource(ItemShopSource shopSource, Dictionary<ItemInfoType, HashSet<string>> bySource, Dictionary<ItemInfoType, Dictionary<string, string>> optionLabels) {
        EnsureSource(shopSource.Type, bySource);
        var npcs = shopSource.Shop.ENpcs.OfType<ENpcBaseRow>().Where(npc => npc.RowId != 0).ToList();
        foreach (var name in npcs.Select(npc => npc.Name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal)) {
            AddSourceOption(shopSource.Type, $"npc-name:{name}", name, bySource, optionLabels);
        }
        foreach (var npc in npcs.Where(npc => string.IsNullOrWhiteSpace(npc.Name))) {
            AddSourceOption(shopSource.Type, $"npc:{npc.RowId}", $"NPC #{npc.RowId}", bySource, optionLabels);
        }

        if (npcs.Count == 0 && shopSource.Shop.RowId != 0) {
            var shopName = shopSource.Shop.Name.Trim();
            AddSourceOption(shopSource.Type, $"shop:{(uint)shopSource.Type}:{shopSource.Shop.RowId}", shopName.Length == 0 ? $"Vendor shop #{shopSource.Shop.RowId}" : shopName, bySource, optionLabels);
        }
    }

    private static void AddSourceOption(ItemInfoType source, string key, string label, Dictionary<ItemInfoType, HashSet<string>> bySource, Dictionary<ItemInfoType, Dictionary<string, string>> optionLabels) {
        var keys = EnsureSource(source, bySource);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(label))
            return;

        keys.Add(key);
        if (!optionLabels.TryGetValue(source, out var labels))
            optionLabels[source] = labels = [];
        labels.TryAdd(key, label.Trim());
    }

    private static HashSet<string> EnsureSource(ItemInfoType source, Dictionary<ItemInfoType, HashSet<string>> bySource) {
        if (!bySource.TryGetValue(source, out var keys))
            bySource[source] = keys = [];
        return keys;
    }

    private static string DutyName(uint contentFinderConditionId)
        => ContentFinderCondition.GetRowRef(contentFinderConditionId) is { IsValid: true, Value.NameFormatted: var name } ? name.ToString().Trim() : string.Empty;

    private static string ItemName(uint itemId)
        => itemId != 0 && Item.GetRowRef(itemId) is { IsValid: true, Value.Name: var name } ? name.ToString().Trim() : string.Empty;

    private static string ToName(ItemInfoType type) {
        if (type == ItemInfoType.CashShop)
            return "Mogstation";
        if (type == ItemInfoType.CraftRecipe)
            return "Crafting";
        if (type == ItemInfoType.PVPSeries)
            return "PvP Series";

        var raw = type.ToString();
        return string.Concat(raw.Select((character, index) => index > 0 && char.IsUpper(character) && (char.IsLower(raw[index - 1]) || index + 1 < raw.Length && char.IsLower(raw[index + 1])) ? $" {character}" : character.ToString()));
    }

    private static uint NumericKeySuffix(string key)
        => uint.TryParse(key.AsSpan(key.LastIndexOf(':') + 1), out var value) ? value : uint.MaxValue;

    private sealed record SourceFilterIndex(
        IReadOnlyDictionary<uint, Dictionary<ItemInfoType, HashSet<string>>> BySetId,
        IReadOnlyDictionary<ItemInfoType, IReadOnlyList<SourceSubFilterOption>> Options,
        IReadOnlyList<ItemInfoType> SourceTypes) {
        internal static SourceFilterIndex Empty { get; } = new(
            new Dictionary<uint, Dictionary<ItemInfoType, HashSet<string>>>(),
            new Dictionary<ItemInfoType, IReadOnlyList<SourceSubFilterOption>>(),
            []);
    }

    // in case I somehow miss a set
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
            IPluginLog.Get().Warning($"[{nameof(CatalogService)}] Coverage gap: source={sourceRowIds.Count}, classified={classifiedRowIds.Count}, missing={missing.Count}. Missing MirageStoreSetItem rowIds: {preview}{suffix}");
        }
        catch (Exception ex) {
            IPluginLog.Get().Error(ex, $"[{nameof(CatalogService)}] mirage coverage diagnostics");
        }
    }
}
