using System.Numerics;
using AllaganLib.GameSheets.ItemSources;
using GlamourLog.Nodes;
using KamiToolKit.Nodes;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GlamourLog.Services;

// maps chest rows to bosses and orders them
// uses a chest model id heurstic for choosing boss chests and then works backwards from position data to order them in the way they're found in the dungeon
internal sealed class DungeonChestLayout {
    // these three ids are the common bronze (regular), silver (intermediate boss) and gold (final boss) chest sgb ids. Most dungeons use these, particular up until newer expacs
    private const uint TreasureSgbRegularChest = 1596;
    private const uint TreasureSgbBossChest = 1597;
    private const uint TreasureSgbFinalBossChest = 1598;

    private readonly Dictionary<uint, uint> _fightNoByChestRowId = [];
    private readonly Dictionary<uint, uint> _maxFightNoByCfcId = [];
    private readonly Dictionary<uint, string> _unmatchedSecondaryLabelByChestRowId = [];
    private readonly Dictionary<uint, Vector3> _positionByChestRowId = [];

    internal static DungeonChestLayout Instance => field ??= Build();

    internal List<uint> OrderChestRowIds(uint cfcId, IEnumerable<uint> chestRowIds) {
        var ids = chestRowIds.Where(id => id != 0).Distinct().ToList();
        if (ids.Count <= 1)
            return ids;

        var points = new List<(uint RowId, Vector3 Position)>(ids.Count);
        foreach (var id in ids)
            points.Add((id, _positionByChestRowId.GetValueOrDefault(id)));

        if (!HasUsablePositions(points))
            return OrderByFightThenRowId(cfcId, ids); // no usable coords yet -> boss order + row id

        var startId = TryGetFinalBossChestRowId(ids) ?? ids[0]; // walk backwards from the final boss chest
        return OrderByNearestNeighborBackwards(points, startId);
    }

    // all chest ids for the set per duty, in dungeon order. labels are 1-based into this list so filtering one piece doesn't renumber chests
    internal Dictionary<uint, List<uint>> BuildDutyChests(CatalogService catalog, GlamourSet set) {
        var cache = Svc.SheetManager.ItemInfoCache;
        var fullScope = catalog.GetSourceScopeItemIds(set, filterPieceId: null).ToHashSet();
        var byCfc = new Dictionary<uint, HashSet<uint>>();
        foreach (var itemId in fullScope) {
            if (cache.GetItemSources(itemId) is not { Count: > 0 } list)
                continue;
            foreach (var src in list) {
                if (src is not ItemDungeonChestSource chest || chest.ContentFinderCondition.RowId == 0) continue;
                if (chest.DungeonChest.RowId is 0) continue;

                if (!byCfc.TryGetValue(chest.ContentFinderCondition.RowId, out var keySet))
                    byCfc[chest.ContentFinderCondition.RowId] = keySet = [];
                keySet.Add(chest.DungeonChest.RowId);
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

    // startRowId should be the final boss since that's the only chest I can be certain of
    // walk from it backwards and assume closest chest by distance is the previous chest in the dungeon, then return this list reversed to get the forward path
    private static List<uint> OrderByNearestNeighborBackwards(List<(uint RowId, Vector3 Position)> chests, uint startRowId) {
        if (chests.Count <= 1)
            return [.. chests.Select(c => c.RowId)];

        var positionById = new Dictionary<uint, Vector3>(chests.Count);
        foreach (var (rowId, position) in chests)
            positionById[rowId] = position;

        if (!positionById.ContainsKey(startRowId))
            startRowId = chests[0].RowId;

        var remaining = new HashSet<uint>(positionById.Keys);
        var reversePath = new List<uint>(chests.Count) { startRowId };
        remaining.Remove(startRowId);
        var current = positionById[startRowId];

        while (remaining.Count > 0) {
            var bestId = 0u;
            var bestDist = float.MaxValue;
            foreach (var id in remaining) {
                var dist = Vector3.DistanceSquared(current, positionById[id]);
                if (dist > bestDist || (dist == bestDist && bestId != 0 && id >= bestId))
                    continue;
                bestDist = dist;
                bestId = id;
            }

            reversePath.Add(bestId);
            remaining.Remove(bestId);
            current = positionById[bestId];
        }

        reversePath.Reverse();
        return reversePath;
    }

    private List<uint> OrderByFightThenRowId(uint cfcId, List<uint> ids)
        => [.. ids.OrderBy(id => _fightNoByChestRowId.GetValueOrDefault(id, _maxFightNoByCfcId.GetValueOrDefault(cfcId) + 1)).ThenBy(id => id)];

    private uint? TryGetFinalBossChestRowId(IEnumerable<uint> chestRowIds) {
        uint? bestId = null;
        var bestFightNo = uint.MinValue;
        foreach (var id in chestRowIds) {
            if (!_fightNoByChestRowId.TryGetValue(id, out var fightNo))
                continue;
            if (bestId is not null && fightNo < bestFightNo)
                continue;
            if (bestId is not null && fightNo == bestFightNo && id >= bestId)
                continue;
            bestId = id;
            bestFightNo = fightNo;
        }

        return bestId;
    }

    private static bool HasUsablePositions(IReadOnlyList<(uint RowId, Vector3 Position)> chests) {
        foreach (var (_, position) in chests) {
            if (position != default)
                return true;
        }

        return false;
    }

    private static DungeonChestLayout Build() {
        var index = new DungeonChestLayout();
        var chests = Svc.Data.GetSupplemental<DungeonChest>(CsvLoader.DungeonChestResourceName).Where(c => c.RowId != 0).ToDictionary(c => c.RowId);

        foreach (var chest in chests.Values)
            index._positionByChestRowId[chest.RowId] = chest.Position;

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

        var maxCountByCfc = byCfcAndSgb.GroupBy(kvp => kvp.Key.CfcId).ToDictionary(g => g.Key, g => g.Max(kvp => kvp.Value.Count));
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

    private static uint? TryGetSgbRowId(uint treasureId)
        => treasureId == 0 || !Treasure.TryGetRow(treasureId, out var treasure) ? null : treasure.SGB.RowId;

    // afaik, basically all dungeons work like this. Only one count: final boss, two count: intermediate bosses, highest count: regular
    // only exception might be something like tam tara that technically has 4 bosses
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
