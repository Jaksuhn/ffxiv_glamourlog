using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows.LogWindow;

namespace GlamourLog;

internal unsafe partial class LogWindow {
    private void PaintDetailsOnly() {
        if (_isFinalizing || !IsOpen || !CanPaintLists())
            return;
        _pendingPaintDetailsOnly = true;
    }

    private void PaintDetailsOnlyNow() {
        if (_isFinalizing || !IsOpen || !CanPaintLists())
            return;
        try {
            RefreshDetails(Svc.Get<OwnershipService>().CaptureSnapshot());
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] {nameof(PaintDetailsOnly)}");
        }
    }

    private void RefreshDetails(OwnershipSnapshot snap) {
        if (_detailRowsListNode is null)
            return;

        if (_selectedSet == null)
            _sourceFilterPieceItemId = null;

        _detailRowOptions.Clear();
        var inventoryItems = snap.InventoryItemIds;

        if (_selectedSet == null) {
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Set Details", IsTopLevelSection = true });
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.JournalHeader, PrimaryText = "No set selected" });
            ApplyCollapsedDetailSections(_detailRowOptions);
            _detailRowsListNode.OptionsList = [.. _detailRowOptions];
            _detailRowsListNode.Update();
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

        var items = _selectedSet.Items;
        if (isCabinetOnly)
            _sourceFilterPieceItemId = null;
        var selectedSetStorageState = Svc.Get<OwnershipService>().GetSetStorageState(_selectedSet, snap);
        foreach (var itemId in items) {
            var storageState = ResolvePieceStorageState(itemId, selectedSetStorageState, snap);
            var iconPart = StorageIconPartFor(storageState);
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.Piece,
                ItemId = itemId,
                PrimaryText = Item.GetRow(itemId).Name.ToString(),
                IsSelected = _sourceFilterPieceItemId == itemId,
                StorageIconPart = iconPart,
                ShowInventoryBadge = iconPart is null && inventoryItems.Contains(itemId),
                ShowArmoireWarning = storageState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose && Svc.Get<CatalogService>().ArmoireItemIds.Contains(itemId),
            });
        }

        if (items.Count > 0 && TryGetCostTotals(_selectedSet, _sourceFilterPieceItemId, out var costTotals)) {
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Costs", IsTopLevelSection = true });
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = _sourceFilterPieceItemId is not null ? "Currencies Required (Single Item)" : "Currencies Required (Full Set)"
            });
            var ordered = costTotals.OrderBy(x => Item.GetRow(x.Key).Name.ToString(), StringComparer.Ordinal).ToList();
            foreach (var kv in ordered) {
                var owned = GetOwnedCurrencyCount(kv.Key);
                var (costNav, costTip, npcName, shopName) = SourcesPanelBuilder.GetShopVendorHintForCostCurrency(Svc.Get<CatalogService>(), _selectedSet, _sourceFilterPieceItemId, kv.Key);
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

        _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Sources", IsTopLevelSection = true });
        SourcesPanelBuilder.AppendSourceRows(Svc.Get<CatalogService>(), _selectedSet, _sourceFilterPieceItemId, _detailRowOptions);
        ApplyCollapsedDetailSections(_detailRowOptions);
        _detailRowsListNode.OptionsList = [.. _detailRowOptions];
        _detailRowsListNode.Update();
    }

    private void OnDetailSectionToggle(string sectionTitle, bool expandSection) {
        if (_isFinalizing || !IsOpen)
            return;
        if (expandSection)
            _collapsedDetailSections.Remove(sectionTitle);
        else
            _collapsedDetailSections.Add(sectionTitle);
        // defer list rebuild so OptionsList is never replaced during this click stack (same as row hit path)
        PaintDetailsOnly();
    }

    /// <summary> Keeps section header rows; drops content under collapsed sections. Nesting follows <see cref="DetailListRowData.IsTopLevelSection"/>. </summary>
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
            foreach (var (cid, amt) in Svc.Get<CatalogService>().GetPrimaryItemCosts(itemId, Svc.Get<CatalogService>().CategoryNameForPrimaryCostLookup(set))) {
                totals.TryGetValue(cid, out var t);
                totals[cid] = t + amt;
            }
        }
        return totals.Count > 0;
    }

    private static int GetOwnedCurrencyCount(uint costItemId) {
        if (costItemId is not 1 && Svc.Get<AllaganToolsIpc>().TryGetOwnedCount(costItemId, out var allaganCount)) // don't use AT for gil since it returns a uint and you can overflow that
            return allaganCount;
        return CurrencyManager.Instance()->SpecialItemBucket.TryGetValue(costItemId, out var value, true)
            ? (int)value.Count
            : InventoryManager.Instance()->GetInventoryItemCount(costItemId);
    }

    private static GlamourIconNode.IconPart? StorageIconPartFor(ItemStorageState storageState)
        => storageState switch {
            ItemStorageState.Armoire => GlamourIconNode.IconPart.Armoire,
            ItemStorageState.DresserLoose => GlamourIconNode.IconPart.DresserFaded,
            ItemStorageState.DresserSet => GlamourIconNode.IconPart.Dresser,
            _ => null,
        };

    private ItemStorageState ResolvePieceStorageState(uint itemId, SetStorageState setStorageState, OwnershipSnapshot snap)
        => Svc.Get<OwnershipService>().GetPieceDisplayStorageState(itemId, _selectedSet!, setStorageState, snap);

    private static void OnCraftRecipeJournalLeftClick(uint recipeRowId) {
        if (recipeRowId == 0)
            return;
        AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeRowId);
    }

    private void OnDetailPieceItemLeftClick(uint itemId) {
        if (_isFinalizing || _selectedSet?.NonSetCabinetPiece == true)
            return;
        _sourceFilterPieceItemId = _sourceFilterPieceItemId == itemId ? null : itemId;
        _pendingPaintDetailsOnly = true;
    }
}
