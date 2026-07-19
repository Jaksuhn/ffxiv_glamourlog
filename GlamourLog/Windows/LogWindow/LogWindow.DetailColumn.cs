using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows.LogWindow;

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
            RefreshDetails(Svc.Get<OwnershipService>().Query());
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

        _detailRowOptions.Clear();

        if (_selectedSet == null) {
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Set Details", IsTopLevelSection = true });
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.JournalHeader, PrimaryText = "No set selected" });
            ApplyCollapsedDetailSections(_detailRowOptions);
            DetailList.AssignOptionsList([.. _detailRowOptions]);
            DetailList.Update();
            return;
        }

        var isCabinetOnly = _selectedSet.NonSetCabinetPiece;
        var setJournalLine = isCabinetOnly || !string.IsNullOrWhiteSpace(_selectedSet.Name)
            ? _selectedSet.Name
            : Item.GetRow(_selectedSet.ItemId).Name.ToString();
        _detailRowOptions.Add(new DetailListRowData {
            Kind = DetailRowKind.SectionHeader,
            PrimaryText = isCabinetOnly ? "Item Details" : "Set Details",
            IsTopLevelSection = true,
        });
        _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.JournalHeader, PrimaryText = setJournalLine });

        var status = q.For(_selectedSet);
        if (isCabinetOnly)
            _selectedSourcePieceItemId = null;
        foreach (var piece in status.Pieces) {
            var iconPart = StorageIconPartFor(piece.BadgeLocation);
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.Piece,
                ItemId = piece.ItemId,
                PrimaryText = Item.GetRow(piece.ItemId).Name.ToString(),
                IsSelected = _selectedSourcePieceItemId == piece.ItemId,
                StorageIconPart = iconPart,
                ShowInventoryBadge = iconPart is null && piece.Location is PieceLocation.Inventory,
                ShowArmoireWarning = piece.ShowArmoireWarning,
            });
        }

        var items = _selectedSet.Items;
        if (items.Count > 0 && TryGetCostTotals(_selectedSet, _selectedSourcePieceItemId, out var costTotals)) {
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Costs", IsTopLevelSection = true });
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = _selectedSourcePieceItemId is not null ? "Currencies Required (Single Item)" : "Currencies Required (Full Set)"
            });
            var ordered = costTotals.OrderBy(x => Item.GetRow(x.Key).Name.ToString(), StringComparer.Ordinal).ToList();
            foreach (var kv in ordered) {
                var owned = OwnershipService.GetOwnedCurrencyCount(kv.Key);
                var (costNav, costTip, npcName, shopName) = SourcesPanelBuilder.FindVendorForCurrency(Svc.Get<CatalogService>(), _selectedSet, _selectedSourcePieceItemId, kv.Key);
                var currencyName = Item.GetRow(kv.Key).Name.ToString().Trim();
                _detailRowOptions.Add(new DetailListRowData {
                    Kind = DetailRowKind.Cost,
                    ItemId = kv.Key,
                    PrimaryText = Item.GetRow(kv.Key).Name.ToString(),
                    SecondaryText = $"Obt. {owned}/{kv.Value}",
                    NavigateTarget = costNav,
                    CostVendorTextTooltip = costTip,
                    CostMapFlagLabel = costNav is not null && npcName.Length > 0 && shopName.Length > 0 ? $"{currencyName} - {npcName} - {shopName}" : string.Empty,
                });
            }
        }

        var sourcesStartIndex = _detailRowOptions.Count;
        SourcesPanelBuilder.AppendSourceRows(Svc.Get<CatalogService>(), _selectedSet, _selectedSourcePieceItemId, _detailRowOptions, DetailList.DutyChestMeasureNode);
        if (_detailRowOptions.Count > sourcesStartIndex) { // only add header when something was appended
            _detailRowOptions.Insert(sourcesStartIndex, new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Sources", IsTopLevelSection = true });
        }
        AppendSharedModelsSection(q);
        ApplyCollapsedDetailSections(_detailRowOptions);
        DetailList.AssignOptionsList([.. _detailRowOptions]);
        DetailList.Update();
    }

    private void OnDetailSectionToggle(string sectionTitle, bool expandSection) {
        if (!IsOpen)
            return;
        if (expandSection)
            _collapsedDetailSections.Remove(sectionTitle);
        else
            _collapsedDetailSections.Add(sectionTitle);
        // defer list rebuild so OptionsList is never replaced during this click stack (same as row hit path)
        PaintDetailsOnly();
    }

    // strip rows that sit under a collapsed section header (keeps the headers themselves)
    private void ApplyCollapsedDetailSections(List<DetailListRowData> rows) {
        if (_collapsedDetailSections.Count == 0)
            return;

        var suppressUnderTopLevel = false;
        var suppressUnderInner = false;

        var write = 0;
        for (var read = 0; read < rows.Count; read++) {
            var row = rows[read];
            if (row.Kind != DetailRowKind.SectionHeader) {
                if (suppressUnderTopLevel || suppressUnderInner)
                    continue;
                rows[write++] = row;
                continue;
            }

            if (row.IsTopLevelSection) {
                suppressUnderTopLevel = _collapsedDetailSections.Contains(row.PrimaryText);
                suppressUnderInner = false;
                rows[write++] = row;
                continue;
            }

            if (suppressUnderTopLevel)
                continue;

            suppressUnderInner = _collapsedDetailSections.Contains(row.PrimaryText);
            rows[write++] = row;
        }

        if (write < rows.Count)
            rows.RemoveRange(write, rows.Count - write);
    }

    private bool TryGetCostTotals(GlamourSet set, uint? pieceFilterPieceItemId, out Dictionary<uint, uint> totals) {
        totals = [];
        IEnumerable<uint> pieceIds = pieceFilterPieceItemId is { } only ? [only] : set.Items;
        foreach (var itemId in pieceIds) {
            foreach (var (cid, amt) in Svc.Get<CatalogService>().GetPrimaryItemCosts(itemId, Svc.Get<CatalogService>().GetCategoryForPreferredCost(set))) {
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

    private static void OnCraftRecipeJournalLeftClick(uint recipeRowId) {
        if (recipeRowId == 0)
            return;
        AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeRowId);
    }

    private void OnDetailPieceItemLeftClick(uint itemId) {
        if (_selectedSet?.NonSetCabinetPiece == true)
            return;
        _selectedSourcePieceItemId = _selectedSourcePieceItemId == itemId ? null : itemId;
        _pendingPaintDetailsOnly = true;
    }

    private void AppendSharedModelsSection(OwnershipQuery q) {
        if (_selectedSet is null)
            return;

        var catalog = Svc.Get<CatalogService>();

        if (_selectedSourcePieceItemId is { } pieceId) {
            var itemSiblings = catalog.GetSharedModelItemSiblings(pieceId);
            if (itemSiblings.Count == 0)
                return;

            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.SectionHeader,
                PrimaryText = "Shared Models",
                IsTopLevelSection = true,
            });
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = "Items with this appearance",
            });

            foreach (var itemId in itemSiblings) {
                var set = catalog.FindCatalogSetForItem(itemId);
                if (set is null)
                    continue;
                _detailRowOptions.Add(new DetailListRowData {
                    Kind = DetailRowKind.SharedModelSet,
                    SharedModelItemId = itemId,
                    SharedModelRow = BuildSharedModelItemRow(itemId, q),
                });
            }
            return;
        }

        var siblings = catalog.GetSharedModelSiblings(_selectedSet);
        if (siblings.Count == 0)
            siblings = catalog.GetPartialSharedModelSetSiblings(_selectedSet); // exact outfit twins first, then piece-level lookalikes
        if (siblings.Count == 0)
            return;

        _detailRowOptions.Add(new DetailListRowData {
            Kind = DetailRowKind.SectionHeader,
            PrimaryText = "Shared Models",
            IsTopLevelSection = true,
        });
        _detailRowOptions.Add(new DetailListRowData {
            Kind = DetailRowKind.JournalHeader,
            PrimaryText = "Sets that contain same-model items",
        });

        foreach (var sibling in siblings) {
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.SharedModelSet,
                SharedModelRow = BuildSetListRowData(sibling, q, appendNotInListSuffix: true),
            });
        }
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
