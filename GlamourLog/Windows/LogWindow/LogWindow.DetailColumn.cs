using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows.LogWindow;
using KamiToolKit.Classes;

namespace GlamourLog;

internal unsafe partial class LogWindow {
    private void PaintDetailsOnly() {
        if (!IsOpen || !CanPaintLists())
            return;
        _pendingPaintDetailsOnly = true;
    }

    private void PaintDetailsOnlyNow() {
        if (!IsOpen || !CanPaintLists())
            return;
        try {
            RefreshDetails(OwnershipService.Get().Query());
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] {nameof(PaintDetailsOnly)}");
        }
    }

    private void RefreshDetails(OwnershipQuery q) {
        if (DetailList is null)
            return;

        if (_selectedSet == null)
            _selectedSourcePieceItemId = null;

        var sections = new List<TreeListSection<DetailListRowData>>();

        if (_selectedSet == null) {
            sections.Add(new TreeListSection<DetailListRowData> {
                Header = "Set Details",
                Entries = [
                    new DetailListRowData { Kind = DetailRowKind.JournalHeader, PrimaryText = "No set selected" },
                ],
            });
            DetailList.AssignSections(sections);
            DetailList.Update();
            return;
        }

        var isCabinetOnly = _selectedSet.NonSetCabinetPiece;
        var setJournalLine = isCabinetOnly || !string.IsNullOrWhiteSpace(_selectedSet.Name)
            ? _selectedSet.Name
            : Item.GetRow(_selectedSet.ItemId).Name.ToString();

        var detailsEntries = new List<DetailListRowData> {
            new() { Kind = DetailRowKind.JournalHeader, PrimaryText = setJournalLine },
        };

        var status = q.For(_selectedSet);
        if (isCabinetOnly)
            _selectedSourcePieceItemId = null;
        foreach (var piece in status.Pieces) {
            var iconPart = StorageIconPartFor(piece.BadgeLocation);
            detailsEntries.Add(new DetailListRowData {
                Kind = DetailRowKind.Piece,
                ItemId = piece.ItemId,
                PrimaryText = Item.GetRow(piece.ItemId).Name.ToString(),
                IsSelected = _selectedSourcePieceItemId == piece.ItemId,
                StorageIconPart = iconPart,
                ShowInventoryBadge = iconPart is null && piece.Location is PieceLocation.Inventory,
                ShowArmoireWarning = piece.ShowArmoireWarning,
            });
        }

        sections.Add(new TreeListSection<DetailListRowData> {
            Header = isCabinetOnly ? "Item Details" : "Set Details",
            Entries = detailsEntries,
        });

        var items = _selectedSet.Items;
        if (items.Count > 0 && TryGetCostTotals(_selectedSet, _selectedSourcePieceItemId, out var costTotals)) {
            var costEntries = new List<DetailListRowData> {
                new() {
                    Kind = DetailRowKind.JournalHeader,
                    PrimaryText = _selectedSourcePieceItemId is not null ? "Currencies Required (Single Item)" : "Currencies Required (Full Set)",
                },
            };
            var ordered = costTotals.OrderBy(x => Item.GetRow(x.Key).Name.ToString(), StringComparer.Ordinal).ToList();
            foreach (var kv in ordered) {
                var owned = OwnershipService.GetOwnedCurrencyCount(kv.Key);
                var (costNav, costTip, npcName, shopName) = SourcesPanelBuilder.FindVendorForCurrency(CatalogService.Get(), _selectedSet, _selectedSourcePieceItemId, kv.Key);
                var currencyName = Item.GetRow(kv.Key).Name.ToString().Trim();
                costEntries.Add(new DetailListRowData {
                    Kind = DetailRowKind.Cost,
                    ItemId = kv.Key,
                    PrimaryText = Item.GetRow(kv.Key).Name.ToString(),
                    SecondaryText = $"Obt. {owned}/{kv.Value}",
                    NavigateTarget = costNav,
                    CostVendorTextTooltip = costTip,
                    CostMapFlagLabel = costNav is not null && npcName.Length > 0 && shopName.Length > 0 ? $"{currencyName} - {npcName} - {shopName}" : string.Empty,
                });
            }

            sections.Add(new TreeListSection<DetailListRowData> {
                Header = "Costs",
                Entries = costEntries,
            });
        }

        var sourceChildren = SourcesPanelBuilder.BuildSourceSections(
            CatalogService.Get(),
            _selectedSet,
            _selectedSourcePieceItemId,
            DetailList.DutyChestMeasureNode);
        if (sourceChildren.Count > 0) {
            sections.Add(new TreeListSection<DetailListRowData> {
                Header = "Sources",
                Children = sourceChildren,
            });
        }

        if (TryBuildSharedModelsSection(q) is { } sharedSection)
            sections.Add(sharedSection);

        DetailList.AssignSections(sections);
        DetailList.Update();
    }

    private bool TryGetCostTotals(GlamourSet set, uint? pieceFilterPieceItemId, out Dictionary<uint, uint> totals) {
        totals = [];
        IEnumerable<uint> pieceIds = pieceFilterPieceItemId is { } only ? [only] : set.Items;
        foreach (var itemId in pieceIds) {
            foreach (var (cid, amt) in CatalogService.Get().GetPrimaryItemCosts(itemId, CatalogService.Get().GetCategoryForPreferredCost(set))) {
                totals.TryGetValue(cid, out var t);
                totals[cid] = t + amt;
            }
        }
        return totals.Count > 0;
    }

    private static GlamourIconNode.IconPart? StorageIconPartFor(ItemStorageState storageState)
        => storageState switch {
            ItemStorageState.Armoire => GlamourIconNode.IconPart.Armoire,
            ItemStorageState.DresserLoose => GlamourIconNode.IconPart.DresserFaded,
            ItemStorageState.DresserSet => GlamourIconNode.IconPart.Dresser,
            _ => null,
        };

    private void OnDetailPieceItemLeftClick(uint itemId) {
        if (_selectedSet?.NonSetCabinetPiece == true)
            return;
        _selectedSourcePieceItemId = _selectedSourcePieceItemId == itemId ? null : itemId;
        _pendingPaintDetailsOnly = true;
    }

    private TreeListSection<DetailListRowData>? TryBuildSharedModelsSection(OwnershipQuery q) {
        if (_selectedSet is null)
            return null;

        var catalog = CatalogService.Get();

        if (_selectedSourcePieceItemId is { } pieceId) {
            var itemSiblings = catalog.GetSharedModelItemSiblings(pieceId);
            if (itemSiblings.Count == 0)
                return null;

            var entries = new List<DetailListRowData> {
                new() {
                    Kind = DetailRowKind.JournalHeader,
                    PrimaryText = "Items with this appearance",
                },
            };

            foreach (var itemId in itemSiblings) {
                var set = catalog.FindCatalogSetForItem(itemId);
                if (set is null)
                    continue;
                entries.Add(new DetailListRowData {
                    Kind = DetailRowKind.SharedModelSet,
                    SharedModelItemId = itemId,
                    SharedModelRow = BuildSharedModelItemRow(itemId, q),
                });
            }

            return new TreeListSection<DetailListRowData> {
                Header = "Shared Models",
                Entries = entries,
            };
        }

        var siblings = catalog.GetSharedModelSiblings(_selectedSet);
        if (siblings.Count == 0)
            siblings = catalog.GetPartialSharedModelSetSiblings(_selectedSet); // exact outfit twins first, then piece-level lookalikes
        if (siblings.Count == 0)
            return null;

        var setEntries = new List<DetailListRowData> {
            new() {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = "Sets that contain same-model items",
            },
        };

        foreach (var sibling in siblings) {
            setEntries.Add(new DetailListRowData {
                Kind = DetailRowKind.SharedModelSet,
                SharedModelRow = BuildSetListRowData(sibling, q, appendNotInListSuffix: true),
            });
        }

        return new TreeListSection<DetailListRowData> {
            Header = "Shared Models",
            Entries = setEntries,
        };
    }

    private void OnSharedModelItemLeftClick(uint itemId, GlamourSet catalogSet) {
        if (!IsOpen)
            return;

        if (_selectedSourcePieceItemId is not null && _selectedSet?.Items.Contains(itemId) == true) {
            if (_selectedSourcePieceItemId == itemId)
                return;
            _selectedSourcePieceItemId = itemId;
            _pendingPaintDetailsOnly = true;
            return;
        }

        OnSharedModelSetLeftClick(catalogSet);
    }

    private void OnSharedModelSetLeftClick(GlamourSet set) {
        if (!IsOpen)
            return;
        if (ReferenceEquals(_selectedSet, set))
            return;

        var targetCategory = AllCategoryId;
        if (targetCategory != _selectedCategoryId) {
            _selectedCategoryId = targetCategory;
            ClearSetSearchIfActive();
            _pendingResetSetScroll = true;
        }

        _selectedSet = set;
        _selectedSourcePieceItemId = null;
        _pendingSelectSet = set;
        _pendingResetDetailScroll = true;
        _pendingRefreshListsAndDetails = true;
    }
}
