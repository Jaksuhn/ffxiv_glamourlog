using AllaganLib.GameSheets.ItemSources;
using GlamourLog.Nodes;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GlamourLog.Services;

/// <summary>
/// Maps dungeon chest rows to boss fight order by matching loot tables between
/// <see cref="DungeonChestItem"/> and <see cref="DungeonBossChest"/> within a duty.
/// World position will replace <see cref="GetPositionSortKey"/> once supplemental data includes it.
/// </summary>
internal sealed class DungeonChestOrderIndex {
    private const uint TreasureSgbRegularChest = 1596;
    private const uint TreasureSgbBossChest = 1597;
    private const uint TreasureSgbFinalBossChest = 1598;
    private readonly Dictionary<uint, uint> _fightNoByChestRowId = [];
    private readonly Dictionary<uint, uint> _maxFightNoByCfcId = [];
    private readonly Dictionary<uint, DungeonChest> _chestByRowId = [];
    private readonly Dictionary<uint, string> _unmatchedSecondaryLabelByChestRowId = [];

    internal static DungeonChestOrderIndex Instance => field ??= Build();

    internal uint GetMaxFightNo(uint cfcId)
        => _maxFightNoByCfcId.GetValueOrDefault(cfcId);

    internal List<uint> OrderChestRowIds(uint cfcId, IEnumerable<uint> chestRowIds)
        => [.. chestRowIds
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => GetSortFightNo(cfcId, id))
            .ThenBy(GetPositionSortKey)];

    /// <summary> Union of dungeon chest row ids per CFC across the whole set (no piece filter), ordered by boss fight then row id. Chest labels use 1-based index in this list so filtering by one piece does not renumber chests. </summary>
    internal Dictionary<uint, List<uint>> BuildDutyChestRowIdsOrderedByCfc(CatalogService catalog, GlamourSet set) {
        var cache = Svc.SheetManager.ItemInfoCache;
        var fullScope = catalog.GetSourceScopeItemIds(set, costScopePieceItemId: null).ToHashSet();
        var byCfc = new Dictionary<uint, HashSet<uint>>();
        foreach (var itemId in fullScope) {
            if (cache.GetItemSources(itemId) is not { Count: > 0 } list)
                continue;
            foreach (var src in list) {
                if (src is not ItemDungeonChestSource chest || chest.ContentFinderCondition.RowId == 0)
                    continue;
                var cfc = chest.ContentFinderCondition.RowId;
                var ck = chest.DungeonChest.RowId;
                if (ck == 0)
                    continue;
                if (!byCfc.TryGetValue(cfc, out var keySet))
                    byCfc[cfc] = keySet = [];
                keySet.Add(ck);
            }
        }

        var result = new Dictionary<uint, List<uint>>();
        foreach (var (cfc, keys) in byCfc)
            result[cfc] = OrderChestRowIds(cfc, keys);
        return result;
    }

    internal float ComputeMaxLabelColumnWidth(uint cfcId, IReadOnlyList<uint> chestOrder, string? extraPrimaryLabel = null) {
        var max = 0f;
        if (extraPrimaryLabel is { Length: > 0 })
            max = DetailListItemNode.MeasureDutyChestLabelColumnWidth(extraPrimaryLabel, string.Empty);
        for (var i = 0; i < chestOrder.Count; i++) {
            var width = DetailListItemNode.MeasureDutyChestLabelColumnWidth(
                $"Chest {i + 1}",
                FormatSecondaryLabel(chestOrder[i]));
            if (width > max)
                max = width;
        }

        return max;
    }

    internal string FormatSecondaryLabel(uint chestRowId) {
        if (_fightNoByChestRowId.TryGetValue(chestRowId, out var fightNo))
            return $"Boss #{fightNo + 1}";

        return _unmatchedSecondaryLabelByChestRowId.GetValueOrDefault(chestRowId) ?? string.Empty;
    }

    private void AssignUnmatchedSecondaryLabels() {
        var unmatchedByCfcAndSgb = new Dictionary<(uint CfcId, uint SgbRowId), List<uint>>();

        foreach (var (chestRowId, chest) in _chestByRowId) {
            if (_fightNoByChestRowId.ContainsKey(chestRowId))
                continue;
            if (TryGetChestSgbRowId(chestRowId) is not { } sgbRowId)
                continue;

            var key = (chest.ContentFinderConditionId, sgbRowId);
            if (!unmatchedByCfcAndSgb.TryGetValue(key, out var chests))
                unmatchedByCfcAndSgb[key] = chests = [];
            chests.Add(chestRowId);
        }

        foreach (var ((_, sgbRowId), chests) in unmatchedByCfcAndSgb) {
            var label = TryGetKnownUnmatchedLabel(sgbRowId) ?? InferUnmatchedLabelFromModelCount(chests.Count);
            foreach (var chestRowId in chests)
                _unmatchedSecondaryLabelByChestRowId[chestRowId] = label;
        }
    }

    private uint GetSortFightNo(uint cfcId, uint chestRowId) {
        if (_fightNoByChestRowId.TryGetValue(chestRowId, out var fightNo))
            return fightNo;

        // Unmatched chests sort after boss-linked pools until world position can slot them.
        return GetMaxFightNo(cfcId) + 1;
    }

    private static uint GetPositionSortKey(uint chestRowId) => chestRowId;

    private static string? TryGetKnownUnmatchedLabel(uint sgbRowId)
        => sgbRowId switch {
            TreasureSgbRegularChest => "Regular",
            TreasureSgbBossChest => "Boss",
            TreasureSgbFinalBossChest => "Final Boss",
            _ => null,
        };

    private static string InferUnmatchedLabelFromModelCount(int count)
        => count switch {
            1 => "Final Boss",
            2 => "Boss",
            _ => "Regular",
        };

    private uint? TryGetChestSgbRowId(uint dungeonChestRowId) {
        if (!_chestByRowId.TryGetValue(dungeonChestRowId, out var chest) || chest.ChestId == 0)
            return null;
        if (!Treasure.TryGetRow(chest.ChestId, out var treasure))
            return null;
        return treasure.SGB.RowId;
    }

    private static DungeonChestOrderIndex Build() {
        var index = new DungeonChestOrderIndex();

        foreach (var chest in Svc.Data.GetSupplemental<DungeonChest>(CsvLoader.DungeonChestResourceName)) {
            if (chest.RowId == 0)
                continue;
            index._chestByRowId[chest.RowId] = chest;
        }

        foreach (var boss in Svc.Data.GetSupplemental<DungeonBoss>(CsvLoader.DungeonBossResourceName))
            index.TrackMaxFightNo(boss.ContentFinderConditionId, boss.FightNo);

        var itemsByChestRowId = Svc.Data.GetSupplemental<DungeonChestItem>(CsvLoader.DungeonChestItemResourceName)
            .Where(i => i.ChestId != 0 && i.ItemId != 0)
            .GroupBy(i => i.ChestId)
            .ToDictionary(g => g.Key, g => g.Select(i => i.ItemId).ToHashSet());

        var bossLootByCfcAndFight = Svc.Data.GetSupplemental<DungeonBossChest>(CsvLoader.DungeonBossChestResourceName)
            .Where(b => b.ContentFinderConditionId != 0)
            .GroupBy(b => (b.ContentFinderConditionId, b.FightNo))
            .ToDictionary(g => g.Key, g => g.Select(b => b.ItemId).ToHashSet());

        foreach (var ((cfcId, fightNo), _) in bossLootByCfcAndFight)
            index.TrackMaxFightNo(cfcId, fightNo);

        foreach (var (chestRowId, chest) in index._chestByRowId) {
            if (!itemsByChestRowId.TryGetValue(chestRowId, out var itemIds))
                continue;

            foreach (var ((cfcId, fightNo), bossItems) in bossLootByCfcAndFight) {
                if (cfcId != chest.ContentFinderConditionId)
                    continue;
                if (!bossItems.SetEquals(itemIds))
                    continue;
                index._fightNoByChestRowId[chestRowId] = fightNo;
                break;
            }
        }

        index.AssignUnmatchedSecondaryLabels();

        return index;
    }

    private void TrackMaxFightNo(uint cfcId, uint fightNo) {
        if (cfcId == 0)
            return;
        if (!_maxFightNoByCfcId.TryGetValue(cfcId, out var current) || fightNo > current)
            _maxFightNoByCfcId[cfcId] = fightNo;
    }
}
