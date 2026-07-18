using AllaganLib.GameSheets.ItemSources;
using GlamourLog.Nodes;
using KamiToolKit.Nodes;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GlamourLog.Services;

/// <summary>
/// Maps dungeon chest rows to boss fight order via <see cref="DungeonChest.DungeonBossId"/>,
/// gated by treasure SGB model frequency so normal chests that share a boss loot table are excluded.
/// </summary>
internal sealed class DungeonChestOrderIndex {
    private const uint TreasureSgbRegularChest = 1596;
    private const uint TreasureSgbBossChest = 1597;
    private const uint TreasureSgbFinalBossChest = 1598;

    private readonly Dictionary<uint, uint> _fightNoByChestRowId = [];
    private readonly Dictionary<uint, uint> _maxFightNoByCfcId = [];
    private readonly Dictionary<uint, string> _unmatchedSecondaryLabelByChestRowId = [];

    internal static DungeonChestOrderIndex Instance => field ??= Build();

    internal List<uint> OrderChestRowIds(uint cfcId, IEnumerable<uint> chestRowIds)
        => [.. chestRowIds
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => _fightNoByChestRowId.GetValueOrDefault(id, _maxFightNoByCfcId.GetValueOrDefault(cfcId) + 1))
            .ThenBy(id => id)];

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

        return byCfc.ToDictionary(kvp => kvp.Key, kvp => OrderChestRowIds(kvp.Key, kvp.Value));
    }

    internal float ComputeMaxLabelColumnWidth(TextNode measure, IReadOnlyList<uint> chestOrder, string? extraPrimaryLabel = null) {
        var max = 0f;
        if (extraPrimaryLabel is { Length: > 0 })
            max = DetailListItemNode.MeasureDutyChestLabelColumnWidth(measure, extraPrimaryLabel, string.Empty);
        for (var i = 0; i < chestOrder.Count; i++) {
            var width = DetailListItemNode.MeasureDutyChestLabelColumnWidth(
                measure,
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

        return _unmatchedSecondaryLabelByChestRowId.GetValueOrDefault(chestRowId, string.Empty);
    }

    private static DungeonChestOrderIndex Build() {
        var index = new DungeonChestOrderIndex();

        var chests = Svc.Data.GetSupplemental<DungeonChest>(CsvLoader.DungeonChestResourceName)
            .Where(c => c.RowId != 0)
            .ToDictionary(c => c.RowId);

        var fightNoByBossRowId = new Dictionary<uint, uint>();
        foreach (var boss in Svc.Data.GetSupplemental<DungeonBoss>(CsvLoader.DungeonBossResourceName)) {
            if (boss.RowId == 0)
                continue;
            fightNoByBossRowId[boss.RowId] = boss.FightNo;
            if (boss.ContentFinderConditionId != 0) {
                var cfc = boss.ContentFinderConditionId;
                if (!index._maxFightNoByCfcId.TryGetValue(cfc, out var current) || boss.FightNo > current)
                    index._maxFightNoByCfcId[cfc] = boss.FightNo;
            }
        }

        // cfc -> sgb -> chests sharing that model
        var byCfcAndSgb = new Dictionary<(uint CfcId, uint SgbRowId), List<DungeonChest>>();
        foreach (var chest in chests.Values) {
            if (chest.ContentFinderConditionId == 0 || TryGetSgbRowId(chest.TreasureId) is not { } sgbRowId)
                continue;
            var key = (chest.ContentFinderConditionId, sgbRowId);
            if (!byCfcAndSgb.TryGetValue(key, out var list))
                byCfcAndSgb[key] = list = [];
            list.Add(chest);
        }

        var maxCountByCfc = byCfcAndSgb
            .GroupBy(kvp => kvp.Key.CfcId)
            .ToDictionary(g => g.Key, g => g.Max(kvp => kvp.Value.Count));

        foreach (var ((cfcId, sgbRowId), group) in byCfcAndSgb) {
            var maxCount = maxCountByCfc[cfcId];
            var label = LabelForSgb(sgbRowId, group.Count, maxCount);
            var isBossModel = label is not "Regular";

            foreach (var chest in group) {
                if (isBossModel
                    && chest.DungeonBossId != 0
                    && fightNoByBossRowId.TryGetValue(chest.DungeonBossId, out var fightNo)) {
                    index._fightNoByChestRowId[chest.RowId] = fightNo;
                }
                else {
                    index._unmatchedSecondaryLabelByChestRowId[chest.RowId] = label;
                }
            }
        }

        return index;
    }

    private static uint? TryGetSgbRowId(uint treasureId) {
        if (treasureId == 0 || !Treasure.TryGetRow(treasureId, out var treasure))
            return null;
        return treasure.SGB.RowId;
    }

    /// <summary>
    /// Within a duty: count 1 = final boss, count 2 = mid-bosses, highest count (&gt;2) = regular.
    /// Known SGB ids (1596–1598) override the frequency heuristic.
    /// </summary>
    private static string LabelForSgb(uint sgbRowId, int countInDuty, int maxCountInDuty)
        => sgbRowId switch {
            TreasureSgbRegularChest => "Regular",
            TreasureSgbBossChest => "Boss",
            TreasureSgbFinalBossChest => "Final Boss",
            _ when countInDuty == maxCountInDuty && countInDuty > 2 => "Regular",
            _ => countInDuty switch {
                1 => "Final Boss",
                2 => "Boss",
                _ => "Regular",
            },
        };
}
