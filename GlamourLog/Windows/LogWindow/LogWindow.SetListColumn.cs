using System.ComponentModel;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows.LogWindow;

namespace GlamourLog;

internal unsafe partial class LogWindow {
    private void RefreshRows(OwnershipQuery q) {
        if (SetList is null || _statsSetsLine is null || _statsSpaceLine is null)
            return;

        var agent = ItemFinderModule.Instance();
        if (agent is null) {
            _statsSetsLine.String = "\u2014 / \u2014";
            _statsSpaceLine.String = string.Empty;
            return;
        }

        var mirageCatalogSets = Svc.Get<CatalogService>().GlamourSets.Where(s => !s.NonSetCabinetPiece).ToList();
        var ownedMirageSets = mirageCatalogSets.Where(s => q.For(s).IsComplete).ToList();
        var totalObtainable = mirageCatalogSets.Count(x => !x.IsUnobtainable || q.For(x).IsComplete);
        _statsSetsLine.String = $"{ownedMirageSets.Count} / {totalObtainable}";
        _statsSpaceLine.String = $"{ownedMirageSets.Sum(x => x.Items.Count - 1)}";

        _categoryColumn?.UpdateButtonStates(_selectedCategoryId, CategoryRows, q);

        RepopulateSetListFromFilteredRows(q);
    }

    private void RebuildSetListOrderOnly() {
        if (SetList is null)
            return;
        if (ItemFinderModule.Instance() is null)
            return;

        RepopulateSetListFromFilteredRows(Svc.Get<OwnershipService>().Query());
    }

    private void RepopulateSetListFromFilteredRows(OwnershipQuery q) {
        if (SetList is null)
            return;

        var searchRaw = _categoryColumn?.Search.Input.String.ToString() ?? string.Empty;
        var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
        var rows = SetListFilterSort.Apply(searchTrimmed, CategoryRows(_selectedCategoryId), q);

        _setListOptions.Clear();
        foreach (var set in rows) {
            try {
                _setListOptions.Add(BuildSetListRowData(set, q));
            }
            catch (Exception ex) {
                Svc.Log.Error(ex, $"[{nameof(LogWindow)}] Build virtual set row failed");
            }
        }

        SetList.OptionsList = [.. _setListOptions];
        if (_pendingClearSetSelection) {
            _pendingClearSetSelection = false;
            SetList.ClearSelection();
        }
        else {
            SyncSetListSelectionHighlight(); // OptionsList replaces row refs, gotta rebind SelectedItems so the highlight matches _selectedSet
        }

        if (_pendingResetSetScroll) {
            _pendingResetSetScroll = false;
            SetList.ResetScroll();
        }

        if (_pendingSelectSet is { } pendingSet) {
            _pendingSelectSet = null;
            ScrollSetListToSet(pendingSet);
        }
    }

    private void SyncSetListSelectionHighlight() {
        if (SetList is null)
            return;

        SetList.SelectedItems.Clear();
        if (_selectedSet is not { } selected)
            return;

        if (SetList.OptionsList.Find(r => ReferenceEquals(r.Set, selected)) is not null and var row)
            SetList.SelectedItems.Add(row);
    }

    private void ScrollSetListToSet(GlamourSet set) {
        if (SetList is null)
            return;

        var index = SetList.OptionsList.FindIndex(r => r.Set.ItemId == set.ItemId);
        if (index < 0)
            return;

        var stride = (int)(GlamourSetListItemNode.ItemHeight + SetList.ItemSpacing);
        if (stride < 1)
            return;

        var nodeCount = Math.Max(1, (int)(SetList.Height / stride));
        var maxScroll = Math.Max(0, SetList.OptionsList.Count - nodeCount);
        var scroll = Math.Clamp(index, 0, maxScroll);

        SetList.ScrollBarNode.OnValueChanged?.Invoke(scroll * stride);
    }

    private void ClearSetSearchIfActive() {
        if (_categoryColumn is null)
            return;
        var current = _categoryColumn.Search.Input.String.ToString();
        if (string.IsNullOrWhiteSpace(current))
            return;
        _categoryColumn.Search.Input.String = string.Empty;
    }

    private SetListRowData BuildSetListRowData(GlamourSet set, OwnershipQuery q, bool appendNotInListSuffix = false) {
        var status = q.For(set);
        var subtitle = SetSublineText(status);
        if (appendNotInListSuffix) {
            var searchRaw = _categoryColumn?.Search.Input.String.ToString() ?? string.Empty;
            var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
            if (C.HideSharedModels && !SetListFilterSort.IsVisibleInSetList(set, searchTrimmed, CategoryRows(_selectedCategoryId), q))
                subtitle += " · Not in list";
        }

        return new SetListRowData {
            Set = set,
            Title = set.Name,
            Subtitle = subtitle,
            IsOwned = status.IsComplete,
            ShowStorage = status.Storage is SetStorageState.Dresser or SetStorageState.Armoire,
            ShowArmoireWarning = status.ArmoireMisplaced,
            StorageIconPart = status.Storage == SetStorageState.Armoire ? GlamourIconNode.IconPart.Armoire : GlamourIconNode.IconPart.Dresser,
        };
    }

    private SetListRowData BuildSharedModelItemRowData(uint itemId, OwnershipQuery q) {
        var catalog = Svc.Get<CatalogService>();
        var set = catalog.FindCatalogSetForItem(itemId)
            ?? new GlamourSet {
                ItemId = itemId,
                Name = Item.GetRow(itemId).Name.ToString(),
                CategoryName = null,
                IsUnobtainable = false,
                Items = [itemId],
                SortItemLevel = Item.GetRow(itemId).LevelItem.RowId,
                SortPatchNo = 0m,
                NonSetCabinetPiece = true,
                IsIncompatible = false,
                ModelSignature = SetModelSignature.ForMiscSingle(itemId),
                SharedModelGroupSize = 1,
                HasPartialSharedModels = false,
            };

        var piece = q.For(set).Piece(itemId);
        var location = piece?.Location ?? q.Locate(itemId);
        var ownedInStorage = location is PieceLocation.Armoire or PieceLocation.LooseDresser or PieceLocation.OutfitSlot;
        var ownedAnywhere = location is not PieceLocation.None;
        var subtitle = ownedInStorage ? "Obt. 1/1" : ownedAnywhere ? "In inventory" : "Obt. 0/1";

        var storageState = piece?.DisplayStorage ?? location switch {
            PieceLocation.Armoire => ItemStorageState.Armoire,
            PieceLocation.LooseDresser => ItemStorageState.DresserLoose,
            PieceLocation.OutfitSlot => ItemStorageState.DresserSet,
            _ => ItemStorageState.None,
        };

        var iconPart = storageState switch {
            ItemStorageState.Armoire => GlamourIconNode.IconPart.Armoire,
            ItemStorageState.DresserLoose => GlamourIconNode.IconPart.DresserFaded,
            ItemStorageState.DresserSet => GlamourIconNode.IconPart.Dresser,
            _ => GlamourIconNode.IconPart.Dresser,
        };

        return new SetListRowData {
            Set = set,
            Title = Item.GetRow(itemId).Name.ToString(),
            Subtitle = subtitle,
            IsOwned = ownedInStorage,
            ShowStorage = storageState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose or ItemStorageState.Armoire,
            ShowArmoireWarning = piece?.ShowArmoireWarning ?? false,
            StorageIconPart = iconPart,
            IconItemId = itemId,
        };
    }

    private void OnSetListSortModeSelected(GlamourSetSortMode mode) {
        if (C.SetListSortMode == mode)
            return;
        C.SetListSortMode = mode;
        C.SetListSortDirection = mode.DefaultDirection();
        C.Save();
        _setListColumn?.SyncSortDirectionChrome();
        RefreshListsAndDetails();
    }

    private void OnSetListSortDirectionToggle() {
        C.SetListSortDirection = C.SetListSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        C.Save();
        _setListColumn?.SyncSortDirectionChrome();
        if (!IsOpen || !CanPaintLists())
            return;
        _pendingRebuildSetListOrderOnly = true;
    }

    private static string SetSublineText(SetStatus status) {
        var set = status.Set;
        var n = set.Items.Count;
        var c = status.OwnedCount;
        string core;
        if (set.NonSetCabinetPiece) {
            core = status.IsComplete ? "Obt. 1/1" : $"Obt. {c}/1";
        }
        else if (status.IsComplete)
            core = $"Obt. {n}/{n}";
        else if (n == 0)
            core = "Obt. 0/0";
        else if (c == n)
            core = "Completable";
        else
            core = $"Obt. {c}/{n}";

        var sortHint = C.SetListSortMode switch {
            GlamourSetSortMode.Patch => set.SortPatchNo == 0m ? "Patch —" : $"Patch {set.SortPatchNo}",
            GlamourSetSortMode.ItemLevel => set.SortItemLevel == 0 ? "iLvl —" : $"iLvl {set.SortItemLevel}",
            _ => null,
        };
        return sortHint is null ? core : $"{core} · {sortHint}";
    }
}
