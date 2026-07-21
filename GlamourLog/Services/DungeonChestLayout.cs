using AllaganLib.GameSheets.ItemSources;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Nodes;
using KamiToolKit.Nodes;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GlamourLog.Services;

// maps chest rows to bosses and orders them for the sources panel
// kind comes from treasure sgb / boss fight data; path order uses mapid (always ascends) then fightno anchors with nearest-neighbor in the gaps
internal sealed class DungeonChestLayout {
    // these three ids are the common bronze (regular), silver (intermediate boss) and gold (final boss) chest sgb ids. Most dungeons use these, particular up until newer expacs
    private const uint TreasureSgbRegularChest = 1596;
    private const uint TreasureSgbBossChest = 1597;
    private const uint TreasureSgbFinalBossChest = 1598;

    private readonly Dictionary<uint, Chest> _byRowId = [];

    internal enum ChestKind : byte { Regular, Boss, FinalBoss }

    internal readonly record struct Chest(uint RowId, byte ChestNo, uint CfcId, uint MapId, uint TerritoryTypeId, uint TreasureId, uint DungeonBossId, uint SgbRowId, Vector3 Position, ChestKind? Kind = null, uint? FightNo = null) {
        internal uint IconId => Kind switch {
            ChestKind.FinalBoss => 60354,
            ChestKind.Boss => 60355,
            _ => 60356,
        };

        internal string SecondaryLabel => Kind switch {
            ChestKind.FinalBoss => "Final Boss",
            ChestKind.Boss => FightNo is { } num ? $"Boss #{num + 1}" : "Boss",
            ChestKind.Regular => "Regular",
            _ => "Unk Type",
        };

        internal bool HasMapMarker => MapId != 0 && TerritoryTypeId != 0 && Position != default;

        internal Vector3 MapPosition {
            get {
                var row = Map.GetRow(MapId);
                return new((Position.X + row.OffsetX), Position.Y, (Position.Z + row.OffsetY));
            }
        }

        internal unsafe void OpenMap(string? title = null) {
            if (!HasMapMarker || AgentMap.Instance() is not (not null and var agent))
                return;
            var name = string.IsNullOrWhiteSpace(title) ? SecondaryLabel.Length > 0 ? $"{SecondaryLabel} Chest" : "Chest" : title;
            Svc.Log.Info($"Opening map (m:{MapId};t:{TerritoryTypeId}) with coords: {MapPosition}");
            agent->OpenMap(MapId, TerritoryTypeId, name);
            agent->ResetMapMarkers();
            agent->SetFlagMapMarker(TerritoryTypeId, MapId, Position, IconId); // TODO: AddMapMarker?
            //agent->AddMapMarker(MapPosition, IconId);
        }
    }

    internal static DungeonChestLayout Instance => field ??= Build();

    internal bool TryGet(uint chestRowId, out Chest chest)
        => _byRowId.TryGetValue(chestRowId, out chest);

    // dungeon order for numbering: map floors first, then boss-anchored nearest-neighbour within each floor
    internal List<uint> OrderChestRowIds(IEnumerable<uint> chestRowIds) {
        var ids = chestRowIds.Where(id => id != 0).Distinct().ToList();
        if (ids.Count <= 1)
            return ids;

        // mapid only ever increases through a dungeon (I think), so floors are a hard outer sort
        var mapGroups = ids
            .Where(id => _byRowId.ContainsKey(id))
            .GroupBy(id => _byRowId[id].MapId)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(id => _byRowId[id]).ToList())
            .ToList();
        if (mapGroups.Count == 0)
            return ids;

        var ordered = new List<uint>(ids.Count);
        Vector3? entrance = null; // last chest of previous map — used to side-split regulars on the next floor
        for (var i = 0; i < mapGroups.Count; i++) {
            var group = mapGroups[i];
            // exit hint: next floor's chest centroid, or final boss on the last floor
            Vector3? exit = i + 1 < mapGroups.Count ? CentroidXz(mapGroups[i + 1]) : TryFindFinalBoss(group)?.Position;

            var mapOrder = OrderMapGroup(group, entrance, exit);
            ordered.AddRange(mapOrder);
            entrance = mapOrder.Count > 0 ? Pos(mapOrder[^1]) : null;
        }

        // any leftovers are thrown at the end. shouldn't happen though
        foreach (var id in ids) {
            if (!ordered.Contains(id))
                ordered.Add(id);
        }

        return ordered;
    }

    // all chests for duties that drop set pieces, in dungeon order (includes empty chests so numbering stays stable)
    internal Dictionary<uint, List<uint>> BuildDutyChests(CatalogService catalog, GlamourSet set) {
        var cache = Svc.SheetManager.ItemInfoCache;
        var fullScope = catalog.GetSourceScopeItemIds(set, filterPieceId: null).ToHashSet();
        var cfcIds = new HashSet<uint>();
        foreach (var itemId in fullScope) {
            if (cache.GetItemSources(itemId) is not { Count: > 0 } list)
                continue;
            foreach (var src in list) {
                if (src is not ItemDungeonChestSource chest || chest.ContentFinderCondition.RowId == 0) continue;
                if (chest.DungeonChest.RowId is 0) continue;
                cfcIds.Add(chest.ContentFinderCondition.RowId);
            }
        }

        return cfcIds.ToDictionary(cfcId => cfcId, OrderChestRowIdsForCfc);
    }

    internal List<uint> OrderChestRowIdsForCfc(uint cfcId)
        => OrderChestRowIds(_byRowId.Values.Where(c => c.CfcId == cfcId).Select(c => c.RowId));

    internal float ComputeMaxLabelColumnWidth(TextNode measure, IReadOnlyList<uint> chestOrder, string? extraPrimaryLabel = null) {
        var max = extraPrimaryLabel is { Length: > 0 } ? DetailListItemNode.MeasureDutyChestLabelColumnWidth(measure, extraPrimaryLabel, string.Empty) : 0f;
        for (var i = 0; i < chestOrder.Count; i++) {
            var width = DetailListItemNode.MeasureDutyChestLabelColumnWidth(measure, $"Chest {i + 1}", FormatSecondaryLabel(chestOrder[i]));
            if (width > max)
                max = width;
        }

        return max;
    }

    internal string FormatSecondaryLabel(uint chestRowId)
        => _byRowId.TryGetValue(chestRowId, out var chest) ? chest.SecondaryLabel : string.Empty;

    private Vector3 Pos(uint rowId) => _byRowId[rowId].Position;

    // one map floor: fightno bosses are fixed waypoints; regulars fill the gaps via nearest corridor
    private List<uint> OrderMapGroup(List<Chest> group, Vector3? entrance, Vector3? exit) {
        if (group.Count <= 1)
            return [.. group.Select(c => c.RowId)];

        var anchors = BuildAnchors(group);
        // need 2+ bosses, or one boss with both entrance+exit, to know before vs after. otherwise just walk back from the end
        if (anchors.Count < 2 && (entrance is null || exit is null || anchors.Count == 0)) {
            var end = anchors.Count > 0 ? anchors[^1] : TryFindFinalBoss(group)?.RowId ?? (exit is { } e ? Nearest(group, e).RowId : group[0].RowId);
            return NearestNeighbor(group.ToDictionary(c => c.RowId, c => c.Position), Pos(end), end, reverse: true);
        }

        // corridors: entrance->A0, A0->A1, ..., An->exit, each regular snaps to the closest one
        var corridors = new (Vector3 Start, Vector3 End)[anchors.Count + 1];
        corridors[0] = (entrance ?? Pos(anchors[0]), Pos(anchors[0]));
        for (var a = 0; a < anchors.Count - 1; a++)
            corridors[a + 1] = (Pos(anchors[a]), Pos(anchors[a + 1]));
        corridors[^1] = (Pos(anchors[^1]), exit ?? Pos(anchors[^1]));

        var segments = Enumerable.Range(0, corridors.Length).Select(_ => new List<uint>()).ToArray();
        var anchorSet = anchors.ToHashSet();
        foreach (var regular in group.Where(c => !anchorSet.Contains(c.RowId))) {
            var best = 0;
            var bestDist = float.MaxValue;
            for (var s = 0; s < corridors.Length; s++) {
                var d = DistToSegmentSq(regular.Position, corridors[s].Start, corridors[s].End);
                if (d < bestDist || (d == bestDist && s < best)) {
                    bestDist = d;
                    best = s;
                }
            }

            segments[best].Add(regular.RowId);
        }

        // stitch: ...regulars -> boss -> ...regulars -> boss... then anything after the last boss toward the exit
        var result = new List<uint>(group.Count);
        for (var s = 0; s < segments.Length; s++) {
            if (s < anchors.Count) {
                var pts = segments[s].ToDictionary(id => id, Pos);
                pts[anchors[s]] = Pos(anchors[s]);
                result.AddRange(NearestNeighbor(pts, Pos(anchors[s]), anchors[s], reverse: true));
            }
            else if (segments[s].Count > 0) {
                result.AddRange(NearestNeighbor(segments[s].ToDictionary(id => id, Pos), corridors[s].Start, forceFirst: null, reverse: false));
            }
        }

        return result;
    }

    // fightno is authoritative for midboss order; gold final may only have Kind and still needs to be last
    private List<uint> BuildAnchors(List<Chest> group) {
        var anchors = group.Where(c => c.FightNo is not null).OrderBy(c => c.FightNo).ThenBy(c => c.RowId).Select(c => c.RowId).ToList();
        if (TryFindFinalBoss(group) is { RowId: var finalId } && !anchors.Contains(finalId))
            anchors.Add(finalId);
        return anchors;
    }

    // prefer Kind=FinalBoss (sgb gold even without DungeonBossId); else highest FightNo (sometimes chests just lack a FightNo)
    private static Chest? TryFindFinalBoss(List<Chest> group) {
        Chest? bestKind = null;
        foreach (var c in group) {
            if (c.Kind is not ChestKind.FinalBoss)
                continue;
            if (bestKind is null || (c.FightNo ?? 0) > (bestKind.Value.FightNo ?? 0) || ((c.FightNo ?? 0) == (bestKind.Value.FightNo ?? 0) && c.RowId < bestKind.Value.RowId))
                bestKind = c;
        }

        if (bestKind is not null)
            return bestKind;

        return group.Where(c => c.FightNo is not null).OrderByDescending(c => c.FightNo).ThenBy(c => c.RowId).Cast<Chest?>().FirstOrDefault();
    }

    private static Vector3 CentroidXz(List<Chest> chests) {
        var x = 0f;
        var z = 0f;
        foreach (var c in chests) {
            x += c.Position.X;
            z += c.Position.Z;
        }

        return new(x / chests.Count, 0f, z / chests.Count);
    }

    private static Chest Nearest(List<Chest> group, Vector3 target)
        => group.MinBy(c => (DistXzSq(c.Position, target), c.RowId));

    // ignoring Y. I don't think it matters
    private static float DistXzSq(Vector3 a, Vector3 b) {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static float DistToSegmentSq(Vector3 p, Vector3 a, Vector3 b) {
        var abx = b.X - a.X;
        var abz = b.Z - a.Z;
        var lenSq = abx * abx + abz * abz;
        if (lenSq < 1e-6f)
            return DistXzSq(p, a);
        var t = Math.Clamp(((p.X - a.X) * abx + (p.Z - a.Z) * abz) / lenSq, 0f, 1f);
        return DistXzSq(p, new(a.X + t * abx, 0f, a.Z + t * abz));
    }

    // greedy nearest-neighbour from start, forceFirst seeds the path. reverse=true when we started at the end (boss) so the list reads forward through the dungeon
    private static List<uint> NearestNeighbor(Dictionary<uint, Vector3> pos, Vector3 start, uint? forceFirst, bool reverse) {
        if (pos.Count <= 1)
            return [.. pos.Keys];

        var remaining = pos.Keys.ToHashSet();
        var path = new List<uint>(remaining.Count);
        var current = start;
        if (forceFirst is { } first && remaining.Remove(first)) {
            path.Add(first);
            current = pos[first];
        }

        while (remaining.Count > 0) {
            var best = remaining.MinBy(id => (DistXzSq(current, pos[id]), id));
            path.Add(best);
            remaining.Remove(best);
            current = pos[best];
        }

        if (reverse)
            path.Reverse();
        return path;
    }

    private static DungeonChestLayout Build() {
        var sheetChests = Svc.Data.GetSupplemental<DungeonChest>(CsvLoader.DungeonChestResourceName).Where(c => c.RowId != 0).ToDictionary(c => c.RowId);
        var fightNoByBossRowId = new Dictionary<uint, uint>();
        var maxFightNoByCfcId = new Dictionary<uint, uint>();
        foreach (var boss in Svc.Data.GetSupplemental<DungeonBoss>(CsvLoader.DungeonBossResourceName)) {
            if (boss.RowId == 0)
                continue;
            fightNoByBossRowId[boss.RowId] = boss.FightNo;
            if (boss.ContentFinderConditionId != 0) {
                var cfc = boss.ContentFinderConditionId;
                if (!maxFightNoByCfcId.TryGetValue(cfc, out var current) || boss.FightNo > current)
                    maxFightNoByCfcId[cfc] = boss.FightNo;
            }
        }

        // cfc -> sgb -> chests sharing that model (count heuristic for nonstandard sgbs)
        var byCfcAndSgb = new Dictionary<(uint CfcId, uint SgbRowId), List<DungeonChest>>();
        foreach (var chest in sheetChests.Values) {
            if (chest.ContentFinderConditionId == 0 || TryGetSgbRowId(chest.TreasureId) is not { } sgbRowId)
                continue;
            var key = (chest.ContentFinderConditionId, sgbRowId);
            if (!byCfcAndSgb.TryGetValue(key, out var list))
                byCfcAndSgb[key] = list = [];
            list.Add(chest);
        }

        var kindByRowId = new Dictionary<uint, ChestKind>();
        var fightNoByRowId = new Dictionary<uint, uint>();

        var maxCountByCfc = byCfcAndSgb.GroupBy(kvp => kvp.Key.CfcId).ToDictionary(g => g.Key, g => g.Max(kvp => kvp.Value.Count));
        foreach (var ((cfcId, sgbRowId), group) in byCfcAndSgb) {
            var kind = KindForSgb(sgbRowId, group.Count, maxCountByCfc[cfcId]);
            var isBossModel = kind is not ChestKind.Regular;

            foreach (var chest in group) {
                // linked dungeon boss -> real fight index; highest fight on the cfc is the final
                if (isBossModel && chest.DungeonBossId != 0 && fightNoByBossRowId.TryGetValue(chest.DungeonBossId, out var fightNo)) {
                    fightNoByRowId[chest.RowId] = fightNo;
                    var maxFight = maxFightNoByCfcId.GetValueOrDefault(cfcId);
                    kindByRowId[chest.RowId] = fightNo >= maxFight ? ChestKind.FinalBoss : ChestKind.Boss;
                }
                else {
                    kindByRowId[chest.RowId] = kind;
                }
            }
        }

        var index = new DungeonChestLayout();
        foreach (var sheet in sheetChests.Values) {
            index._byRowId[sheet.RowId] = new Chest(
                RowId: sheet.RowId,
                ChestNo: sheet.ChestNo,
                CfcId: sheet.ContentFinderConditionId,
                MapId: sheet.MapId,
                TerritoryTypeId: sheet.TerritoryTypeId,
                TreasureId: sheet.TreasureId,
                DungeonBossId: sheet.DungeonBossId,
                SgbRowId: TryGetSgbRowId(sheet.TreasureId) ?? 0,
                Position: sheet.Position,
                Kind: kindByRowId.TryGetValue(sheet.RowId, out var kind) ? kind : null,
                FightNo: fightNoByRowId.TryGetValue(sheet.RowId, out var fightNo) ? fightNo : null);
        }

        return index;
    }

    private static uint? TryGetSgbRowId(uint treasureId)
        => treasureId == 0 || !Treasure.TryGetRow(treasureId, out var treasure) ? null : treasure.SGB.RowId;

    // afaik, basically all dungeons work like this. Only one count: final boss, two count: intermediate bosses, highest count: regular
    // only exception might be something like tam tara that technically has 4 bosses (but that uses standard sgb ids so not an issue here)
    private static ChestKind KindForSgb(uint sgbRowId, int countInDuty, int maxCountInDuty)
        => sgbRowId switch {
            TreasureSgbRegularChest => ChestKind.Regular,
            TreasureSgbBossChest => ChestKind.Boss,
            TreasureSgbFinalBossChest => ChestKind.FinalBoss,
            _ when countInDuty == maxCountInDuty && countInDuty > 2 => ChestKind.Regular,
            _ => countInDuty switch {
                1 => ChestKind.FinalBoss,
                2 => ChestKind.Boss,
                _ => ChestKind.Regular,
            },
        };
}
