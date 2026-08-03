using AllaganLib.GameSheets.Caches;
using AllaganLib.GameSheets.Extensions;
using AllaganLib.GameSheets.ItemSources;
using AllaganLib.GameSheets.Model;
using AllaganLib.GameSheets.Sheets;
using AllaganLib.GameSheets.Sheets.Rows;
using GlamourLog.Nodes;
using GlamourLog.Services;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel;

namespace GlamourLog.Windows.LogWindow;

internal static class SourcesPanelBuilder {
    private const int MaxSourceIconsVisible = 10;

    private static DutyBuckets GetDutyBucket(Dictionary<uint, DutyBuckets> duties, uint cfcId) {
        if (!duties.TryGetValue(cfcId, out var b)) {
            b = new DutyBuckets();
            duties[cfcId] = b;
        }

        return b;
    }

    private static readonly ItemInfoType[] SupplementalCofferTypes = [
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
        ItemInfoType.PagosTreasure,
        ItemInfoType.PyrosTreasure,
        ItemInfoType.HydatosTreasure,
        ItemInfoType.OccultPot,
        ItemInfoType.OccultGoldenCoffer,
        ItemInfoType.Logogram,
        ItemInfoType.PilgrimsTraverse,
        ItemInfoType.Oizys,
    ];

    internal static List<TreeListSection<DetailListRowData>> BuildSourceSections(CatalogService catalog, GlamourSet set, uint? pieceFilter, TextNode dutyChestMeasure) {
        var sections = new List<TreeListSection<DetailListRowData>>();
        var scopeList = catalog.GetSourceScopeItemIds(set, pieceFilter);
        var scope = scopeList.ToHashSet();
        if (scope.Count == 0)
            return sections;

        var acquisition = ItemAcquisitionService.Get();
        var sourcesByPiece = new Dictionary<uint, List<ItemSource>>();
        foreach (var itemId in scope) {
            var list = acquisition.GetSources(itemId);
            if (list.Count > 0)
                sourcesByPiece[itemId] = [.. list];
        }

        var dutyChestRowIdsOrderedByCfc = DungeonChestLayout.Instance.BuildDutyChests(catalog, set);

        TryAddSection(sections, BuildDutiesSection(sourcesByPiece, scope, dutyChestRowIdsOrderedByCfc, dutyChestMeasure));
        TryAddSection(sections, BuildFatesSection(sourcesByPiece, scope));
        TryAddSection(sections, BuildLootboxSection(sourcesByPiece, scope));
        TryAddSection(sections, BuildCraftSection(sourcesByPiece, scope));
        TryAddSection(sections, BuildDesynthesisSection(sourcesByPiece, scope));
        TryAddSection(sections, BuildQuestsSection(sourcesByPiece, scope));
        return sections;
    }

    private static void TryAddSection(List<TreeListSection<DetailListRowData>> sections, TreeListSection<DetailListRowData>? section) {
        if (section is not null)
            sections.Add(section);
    }

    private static TreeListSection<DetailListRowData>? BuildDutiesSection(Dictionary<uint, List<ItemSource>> sourcesByPiece, HashSet<uint> scope, Dictionary<uint, List<uint>> dutyChestRowIdsOrderedByCfc, TextNode dutyChestMeasure) {
        var duties = new Dictionary<uint, DutyBuckets>();
        foreach (var (pieceId, list) in sourcesByPiece) {
            foreach (var src in list) {
                switch (src) {
                    case ItemDungeonChestSource chest when chest.ContentFinderCondition.RowId != 0:
                        var cfc = chest.ContentFinderCondition.RowId;
                        var b = GetDutyBucket(duties, cfc);
                        var ck = chest.DungeonChest.RowId;
                        if (!b.Chests.TryGetValue(ck, out var set)) {
                            set = [];
                            b.Chests[ck] = set;
                        }
                        set.Add(pieceId);
                        break;
                    case ItemDungeonDropSource drop when drop.ContentFinderCondition.RowId != 0:
                        var dropCfc = drop.ContentFinderCondition.RowId;
                        if (ContentFinderCondition.GetRowRef(dropCfc) is { IsValid: true, Value.ContentType.RowId: not 9 }) // exclude treasure dungeons
                            GetDutyBucket(duties, dropCfc).General.Add(pieceId);
                        break;
                }
            }
        }

        if (duties.Count == 0)
            return null;

        var entries = new List<DetailListRowData>();
        foreach (var cfcId in duties.Keys.OrderBy(id => DutyName(id), StringComparer.Ordinal)) {
            if (ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true, Value.NameFormatted: var name })
                continue;
            var dn = name.ToString().Trim();
            if (dn.Length == 0)
                continue;
            var b = duties[cfcId];
            entries.Add(new DetailListRowData {
                Kind = DetailRowKind.SourceDuty,
                PrimaryText = dn,
                ContentFinderConditionId = cfcId,
            });

            var chestIndex = DungeonChestLayout.Instance;
            var fullChestOrder = dutyChestRowIdsOrderedByCfc.GetValueOrDefault(cfcId) ?? chestIndex.OrderChestRowIdsForCfc(cfcId);
            var chestKeysThisDuty = fullChestOrder.Where(b.Chests.ContainsKey).ToList();
            var hasGeneral = b.General.Count > 0;
            var hasChests = chestKeysThisDuty.Count > 0;

            var maxDutyChestLabelWidth = chestIndex.ComputeMaxLabelColumnWidth(dutyChestMeasure, fullChestOrder, extraPrimaryLabel: hasGeneral && hasChests ? "General" : null);

            if (hasGeneral) {
                if (hasChests)
                    AppendIconStripRow(entries, "General", string.Empty, b.General, scope, iconOnly: false, sourceChestLabelColumnWidth: maxDutyChestLabelWidth);
                else
                    AppendIconStripRow(entries, string.Empty, b.General, scope, iconOnly: true);
            }

            foreach (var ck in chestKeysThisDuty) {
                var chestNum = fullChestOrder.IndexOf(ck) + 1;
                var hasChest = chestIndex.TryGet(ck, out var chest);
                AppendIconStripRow(
                    entries,
                    $"Chest {chestNum}",
                    hasChest ? chest.SecondaryLabel : string.Empty,
                    b.Chests[ck],
                    scope,
                    iconOnly: false,
                    sourceChestLabelColumnWidth: maxDutyChestLabelWidth,
                    dungeonChestRowId: hasChest && chest.HasMapMarker ? ck : 0);
            }
        }

        return entries.Count == 0 ? null : new TreeListSection<DetailListRowData> {
            Header = "Duties",
            Entries = entries,
        };
    }

    private static string DutyName(uint cfcId)
        => ContentFinderCondition.GetRowRef(cfcId) is { IsValid: true, Value.NameFormatted: var n } ? n.ToString() : string.Empty;

    private sealed class DutyBuckets {
        internal HashSet<uint> General { get; } = [];
        internal Dictionary<uint, HashSet<uint>> Chests { get; } = [];
    }

    private static TreeListSection<DetailListRowData>? BuildFatesSection(Dictionary<uint, List<ItemSource>> sourcesByPiece, HashSet<uint> scope) {
        var fateItems = new Dictionary<uint, HashSet<uint>>();
        foreach (var (pieceId, list) in sourcesByPiece) {
            foreach (var src in list) {
                if (src is ItemFateSource fate && fate.Fate.RowId != 0) {
                    if (!fateItems.TryGetValue(fate.Fate.RowId, out var set)) {
                        set = [];
                        fateItems[fate.Fate.RowId] = set;
                    }
                    set.Add(pieceId);
                }
            }
        }

        if (fateItems.Count == 0)
            return null;

        var entries = new List<DetailListRowData>();
        foreach (var (fateId, setItems) in fateItems.OrderBy(e => Fate.GetRow(e.Key).Name.ToString(), StringComparer.Ordinal)) {
            var fateName = Fate.GetRow(fateId).Name.ToString().Trim();
            if (fateName.Length == 0)
                continue;
            entries.Add(new DetailListRowData {
                Kind = DetailRowKind.SourceDuty,
                PrimaryText = fateName,
                ContentFinderConditionId = 0,
            });
            AppendIconStripRow(entries, string.Empty, setItems, scope, iconOnly: true);
        }

        return entries.Count == 0 ? null : new TreeListSection<DetailListRowData> {
            Header = "FATEs",
            Entries = entries,
        };
    }

    private static SourceNavigateTarget? TryNavigateTargetFromNpc(ENpcBaseRow npc) {
        foreach (var loc in npc.Locations) {
            if (loc is not NpcLocation n)
                continue;
            if (!n.TerritoryType.IsValid || n.TerritoryType.RowId == 0)
                continue;
            if (!n.AlreadyConverted)
                return new SourceNavigateTarget(n.TerritoryType.RowId, new Vector3((float)n.X, 0f, (float)n.Y));
        }

        return null;
    }

    // first in-world npc shop that sells a set piece for this currency (map pin). mog station text if it's cash-shop only
    internal static (SourceNavigateTarget? NavigateTarget, string VendorTooltip, string NpcName, string ShopName) FindVendorForCurrency(CatalogService catalog, GlamourSet set, uint? costScopePieceItemId, uint currencyItemId) {
        var cat = catalog.GetCategoryForPreferredCost(set);
        var acquisition = ItemAcquisitionService.Get();
        IEnumerable<uint> pieceIds = costScopePieceItemId is { } only ? [only] : set.Items;

        foreach (var pieceId in pieceIds) {
            if (!catalog.GetPrimaryItemCosts(pieceId, cat).Any(c => c.ItemId == currencyItemId))
                continue;
            foreach (var src in acquisition.GetSources(pieceId)) {
                if (src is not ItemShopSource shopSource || !shopSource.Type.IsShop())
                    continue;
                var shop = shopSource.Shop;
                var shopName = shop.Name.Trim();
                if (string.IsNullOrEmpty(shopName))
                    shopName = FormatShopTypeLabel(shopSource.Type);
                foreach (var npc in shop.ENpcs.OfType<ENpcBaseRow>().Where(n => n.RowId != 0).OrderBy(n => n.RowId)) {
                    if (TryNavigateTargetFromNpc(npc) is not { } nav)
                        continue;
                    var npcName = npc.Name.ToString().Trim();
                    if (npcName.Length == 0)
                        npcName = $"NPC #{npc.RowId}";
                    return (nav, $"{npcName}\n{shopName}", npcName, shopName);
                }
            }
        }

        foreach (var pieceId in pieceIds) {
            if (!catalog.GetPrimaryItemCosts(pieceId, cat).Any(c => c.ItemId == currencyItemId))
                continue;
            if (acquisition.GetSources(pieceId).Any(static s => s is ItemCashShopSource)) {
                var cashShop = FormatShopTypeLabel(ItemInfoType.CashShop);
                return (null, $"Mog Station\n{cashShop}", "Mog Station", cashShop);
            }
        }

        return (null, string.Empty, string.Empty, string.Empty);
    }

    // lootboxes / field-op coffers that aren't normal duty chest drops
    private static TreeListSection<DetailListRowData>? BuildLootboxSection(Dictionary<uint, List<ItemSource>> sourcesByPiece, HashSet<uint> scope) {
        var supplement = new Dictionary<ItemInfoType, Dictionary<uint, HashSet<uint>>>();
        var fieldOps = new Dictionary<(ItemInfoType Type, uint CofferKind), HashSet<uint>>();
        foreach (var (pieceId, list) in sourcesByPiece) {
            foreach (var src in list) {
                switch (src) {
                    case ItemSupplementSource sup when SupplementalCofferTypes.Contains(sup.Type) && sup.CostItem is not null && sup.CostItem.RowId != 0: {
                            if (!supplement.TryGetValue(sup.Type, out var byCost)) {
                                byCost = [];
                                supplement[sup.Type] = byCost;
                            }
                            if (!byCost.TryGetValue(sup.CostItem.RowId, out var set)) {
                                set = [];
                                byCost[sup.CostItem.RowId] = set;
                            }

                            set.Add(pieceId);
                            break;
                        }
                    case ItemFieldOpCofferSource field: {
                            var key = (field.Type, (uint)field.CofferType);
                            if (!fieldOps.TryGetValue(key, out var set)) {
                                set = [];
                                fieldOps[key] = set;
                            }

                            set.Add(pieceId);
                            break;
                        }
                }
            }
        }

        if (supplement.Count == 0 && fieldOps.Count == 0)
            return null;

        var entries = new List<DetailListRowData>();
        foreach (var (type, byCost) in supplement.OrderBy(e => HumanizeInfoType(e.Key), StringComparer.Ordinal)) {
            entries.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = HumanizeInfoType(type),
            });
            foreach (var (costId, pieceSet) in byCost.OrderBy(e => Item.GetRow(e.Key).Name.ToString(), StringComparer.Ordinal)) {
                AppendArrowFlowRow(entries, [costId], pieceSet);
            }
        }

        foreach (var (key, pieceSet) in fieldOps.OrderBy(e => HumanizeInfoType(e.Key.Type)).ThenBy(e => e.Key.CofferKind)) {
            entries.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = $"{HumanizeInfoType(key.Type)} ({key.CofferKind})",
            });
            AppendIconStripRow(entries, string.Empty, pieceSet, scope, iconOnly: true);
        }

        return new TreeListSection<DetailListRowData> {
            Header = "Lootboxes",
            Entries = entries,
        };
    }

    private static TreeListSection<DetailListRowData>? BuildCraftSection(Dictionary<uint, List<ItemSource>> sourcesByPiece, HashSet<uint> scope) {
        var byRecipe = new Dictionary<uint, CraftAgg>();
        foreach (var (pieceId, list) in sourcesByPiece) {
            foreach (var src in list) {
                if (src is not ItemCraftResultSource craft)
                    continue;
                var rid = craft.Recipe.RowId;
                if (!byRecipe.TryGetValue(rid, out var agg)) {
                    agg = new CraftAgg { Recipe = craft.Recipe, ResultItemId = craft.Item.RowId };
                    byRecipe[rid] = agg;
                }
            }
        }

        if (byRecipe.Count == 0)
            return null;

        var entries = new List<DetailListRowData>();
        foreach (var (rid, agg) in byRecipe.OrderBy(e => Item.GetRow(e.Value.ResultItemId).Name.ToString(), StringComparer.Ordinal)) {
            var recipeName = Item.GetRow(agg.ResultItemId).Name.ToString().Trim();
            if (recipeName.Length == 0)
                recipeName = $"Recipe #{rid}";
            entries.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = recipeName,
                CraftRecipeRowId = rid,
            });
            var ingIds = new List<uint>();
            foreach (var (ingId, _) in agg.Recipe.IngredientCounts) {
                if (ingId == 0 || Item.GetRow(ingId) is { ItemUICategory.RowId: 59 }) // ignore crystals
                    continue;
                ingIds.Add(ingId);
            }

            var ingOrdered = ingIds.Distinct().OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
            if (ingOrdered.Count > 0)
                AppendIconStripRow(entries, string.Empty, ingOrdered, scope, iconOnly: true, presentation: SourceIconPresentation.Large);
        }

        return new TreeListSection<DetailListRowData> {
            Header = "Crafting",
            Entries = entries,
        };
    }

    private sealed class CraftAgg {
        internal required RecipeRow Recipe { get; init; }
        internal uint ResultItemId { get; init; }
    }

    private static TreeListSection<DetailListRowData>? BuildDesynthesisSection(Dictionary<uint, List<ItemSource>> sourcesByPiece, HashSet<uint> scope) {
        var byCost = new Dictionary<uint, HashSet<uint>>();
        foreach (var (pieceId, list) in sourcesByPiece) {
            foreach (var src in list) {
                if (src is ItemDesynthSource ds && ds.CostItem is { RowId: var cid } && cid != 0) {
                    if (!byCost.TryGetValue(cid, out var set)) {
                        set = [];
                        byCost[cid] = set;
                    }
                    set.Add(pieceId);
                }
            }
        }

        if (byCost.Count == 0)
            return null;

        var entries = new List<DetailListRowData>();
        foreach (var (costId, pieces) in byCost.OrderBy(e => Item.GetRow(e.Key).Name.ToString(), StringComparer.Ordinal)) {
            entries.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = Item.GetRow(costId).Name.ToString(),
            });
            AppendArrowFlowRow(entries, [costId], pieces);
        }

        return new TreeListSection<DetailListRowData> {
            Header = "Desynthesis",
            Entries = entries,
        };
    }

    private static TreeListSection<DetailListRowData>? BuildQuestsSection(Dictionary<uint, List<ItemSource>> sourcesByPiece, HashSet<uint> scope) {
        var byQuest = new Dictionary<uint, QuestAgg>();
        foreach (var (pieceId, list) in sourcesByPiece) {
            foreach (var src in list) {
                if (src is not ItemQuestSource qs)
                    continue;
                if (qs.Quest.RowId == 0)
                    continue;
                var qid = qs.Quest.RowId;
                if (!byQuest.TryGetValue(qid, out var agg)) {
                    var title = qs.Quest.Value.Name.ToString().Trim();
                    if (title.Length == 0)
                        title = $"Quest #{qid}";
                    agg = new QuestAgg {
                        Title = title,
                        NavigateTarget = TryQuestNavigateTarget(qs.Quest),
                    };
                    byQuest[qid] = agg;
                }

                agg.Pieces.Add(pieceId);
            }
        }

        if (byQuest.Count == 0)
            return null;

        var entries = new List<DetailListRowData>();
        foreach (var (_, agg) in byQuest.OrderBy(e => e.Value.Title, StringComparer.Ordinal)) {
            entries.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = agg.Title,
                NavigateTarget = agg.NavigateTarget,
            });
            AppendIconStripRow(entries, string.Empty, agg.Pieces, scope, iconOnly: true);
        }

        return new TreeListSection<DetailListRowData> {
            Header = "Quests",
            Entries = entries,
        };
    }

    private sealed class QuestAgg {
        internal required string Title { get; init; }
        internal SourceNavigateTarget? NavigateTarget { get; init; }
        internal HashSet<uint> Pieces { get; } = [];
    }

    private static SourceNavigateTarget? TryQuestNavigateTarget(RowRef<Quest> questRef) {
        if (questRef.RowId == 0)
            return null;
        var q = questRef.Value;
        var issuer = q.IssuerStart;
        if (issuer.RowId == 0)
            return null;
        var enpcRow = Svc.SheetManager.GetSheet<ENpcBaseSheet>().GetRowOrDefault(issuer.RowId);
        return enpcRow is null ? null : TryNavigateTargetFromNpc(enpcRow);
    }

    private static void AppendIconStripRow(List<DetailListRowData> rows, string label, string secondaryLabel, IEnumerable<uint> itemIds, HashSet<uint> scope, bool iconOnly = false, SourceIconPresentation presentation = SourceIconPresentation.Normal, float sourceChestLabelColumnWidth = 0f, uint dungeonChestRowId = 0) {
        var ordered = itemIds.Where(id => id != 0).Distinct().OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
        if (ordered.Count == 0)
            return;
        var overflow = Math.Max(0, ordered.Count - MaxSourceIconsVisible);
        var visible = ordered.Count <= MaxSourceIconsVisible ? ordered : [.. ordered.Take(MaxSourceIconsVisible)];

        rows.Add(new DetailListRowData {
            Kind = DetailRowKind.SourceChest,
            PrimaryText = label,
            SecondaryText = secondaryLabel,
            SourceItemIds = visible,
            SourceIconsOnly = iconOnly || string.IsNullOrEmpty(label),
            SourceIconOverflow = overflow,
            SourcePresentation = presentation,
            SourceChestLabelColumnWidth = sourceChestLabelColumnWidth,
            DungeonChestRowId = dungeonChestRowId,
        });
    }

    private static void AppendIconStripRow(List<DetailListRowData> rows, string label, IEnumerable<uint> itemIds, HashSet<uint> scope, bool iconOnly = false, SourceIconPresentation presentation = SourceIconPresentation.Normal)
        => AppendIconStripRow(rows, label, string.Empty, itemIds, scope, iconOnly, presentation);

    private static void AppendIconStripRow(List<DetailListRowData> rows, string label, HashSet<uint> itemIds, HashSet<uint> scope, bool iconOnly = false, SourceIconPresentation presentation = SourceIconPresentation.Normal)
        => AppendIconStripRow(rows, label, string.Empty, itemIds, scope, iconOnly, presentation);

    // one-line "left -> right" icon row (desynth / lootbox key -> what you get)
    private static void AppendArrowFlowRow(List<DetailListRowData> rows, IReadOnlyList<uint> leftIds, IEnumerable<uint> rightIds) {
        var leftOrdered = leftIds.Where(id => id != 0).Distinct().OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
        var rightOrdered = rightIds.Where(id => id != 0).Distinct().OrderBy(id => Item.GetRow(id).Name.ToString(), StringComparer.Ordinal).ToList();
        if (leftOrdered.Count == 0 && rightOrdered.Count == 0)
            return;
        var overflow = Math.Max(0, rightOrdered.Count - MaxSourceIconsVisible);
        var rightVisible = rightOrdered.Count <= MaxSourceIconsVisible ? rightOrdered : [.. rightOrdered.Take(MaxSourceIconsVisible)];
        rows.Add(new DetailListRowData {
            Kind = DetailRowKind.SourceArrowFlow,
            SourceFlowLeftIds = leftOrdered,
            SourceItemIds = rightVisible,
            SourceIconOverflow = overflow,
        });
    }

    private static string HumanizeInfoType(ItemInfoType t)
        => t.ToString().Replace("Shop", " Shop", StringComparison.Ordinal);

    private static string FormatShopTypeLabel(ItemInfoType type)
        => type.ToString().Replace("Shop", " Shop", StringComparison.Ordinal);
}
