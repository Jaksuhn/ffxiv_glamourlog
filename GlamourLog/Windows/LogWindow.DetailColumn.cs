using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows;

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
            RefreshDetails(Svc.Get<OwnershipService>().GetOwnedItems());
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] {nameof(PaintDetailsOnly)}");
        }
    }

    private void RefreshDetails(HashSet<uint> ownedItems) {
        if (_detailRowsListNode is null)
            return;

        if (_selectedSet == null)
            _sourceFilterPieceItemId = null;

        _detailRowOptions.Clear();
        var inventoryItems = Svc.Get<OwnershipService>().GetInventoryItemsOnly();

        if (_selectedSet == null) {
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Set Details" });
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.JournalHeader, PrimaryText = "No set selected" });
            _detailRowsListNode.OptionsList = [.. _detailRowOptions];
            _detailRowsListNode.Update();
            return;
        }

        var setJournalLine = string.IsNullOrWhiteSpace(_selectedSet.Name) ? Item.GetRow(_selectedSet.ItemId).Name.ToString() : _selectedSet.Name;
        _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Set Details" });
        _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.JournalHeader, PrimaryText = setJournalLine });

        var items = _selectedSet.Items;
        var selectedSetStorageState = Svc.Get<OwnershipService>().GetSetStorageState(_selectedSet, ownedItems);
        foreach (var itemId in items) {
            var storageState = ResolvePieceStorageState(itemId, selectedSetStorageState);
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
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Costs" });
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

        _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Sources" });
        SourcesPanelBuilder.AppendSourceRows(Svc.Get<CatalogService>(), _selectedSet, _sourceFilterPieceItemId, _detailRowOptions);
        _detailRowsListNode.OptionsList = [.. _detailRowOptions];
        _detailRowsListNode.Update();
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

    private static int GetOwnedCurrencyCount(uint costItemId)
        => CurrencyManager.Instance()->SpecialItemBucket.TryGetValue(costItemId, out var value, true) ? (int)value.Count : InventoryManager.Instance()->GetInventoryItemCount(costItemId);

    private static GlamourIconNode.IconPart? StorageIconPartFor(ItemStorageState storageState)
        => storageState switch {
            ItemStorageState.Armoire => GlamourIconNode.IconPart.Armoire,
            ItemStorageState.DresserLoose => GlamourIconNode.IconPart.DresserFaded,
            ItemStorageState.DresserSet => GlamourIconNode.IconPart.Dresser,
            _ => null,
        };

    private ItemStorageState ResolvePieceStorageState(uint itemId, SetStorageState setStorageState)
        => Svc.Get<OwnershipService>().GetPieceDisplayStorageState(itemId, _selectedSet!, setStorageState);

    private static void OnCraftRecipeJournalLeftClick(uint recipeRowId) {
        if (recipeRowId == 0)
            return;
        AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeRowId);
    }

    private void OnDetailPieceItemLeftClick(uint itemId) {
        if (_isFinalizing)
            return;
        _sourceFilterPieceItemId = _sourceFilterPieceItemId == itemId ? null : itemId;
        _pendingPaintDetailsOnly = true;
    }
}
