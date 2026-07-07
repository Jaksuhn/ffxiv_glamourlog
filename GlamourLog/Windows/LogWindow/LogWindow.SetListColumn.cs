using System.ComponentModel;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows.LogWindow;

namespace GlamourLog;

internal unsafe partial class LogWindow {
    private void RefreshRows(OwnershipSnapshot snap) {
        if (SetList is null || _statsSetsLine is null || _statsSpaceLine is null)
            return;

        var agent = ItemFinderModule.Instance();
        if (agent is null) {
            _statsSetsLine.String = "\u2014 / \u2014";
            _statsSpaceLine.String = string.Empty;
            return;
        }

        var mirageCatalogSets = Svc.Get<CatalogService>().GlamourSets.Where(s => !s.NonSetCabinetPiece);
        var ownedMirageSets = snap.OwnedSets.Where(s => !s.NonSetCabinetPiece);
        var totalObtainable = mirageCatalogSets.Count(x => !x.IsUnobtainable || snap.OwnedSets.Contains(x));
        _statsSetsLine.String = $"{ownedMirageSets.Count()} / {totalObtainable}";
        _statsSpaceLine.String = $"{ownedMirageSets.Sum(x => x.Items.Count - 1)}";

        _categoryColumn?.UpdateButtonStates(_selectedCategoryId, CategoryRows, snap);

        RepopulateSetListFromFilteredRows(snap);
    }

    private void RebuildSetListOrderOnly() {
        if (SetList is null)
            return;
        if (ItemFinderModule.Instance() is null)
            return;

        RepopulateSetListFromFilteredRows(Svc.Get<OwnershipService>().CaptureSnapshot());
    }

    private void RepopulateSetListFromFilteredRows(OwnershipSnapshot snap) {
        if (SetList is null)
            return;

        var searchRaw = _categoryColumn?.Search.Input.String.ToString() ?? string.Empty;
        var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
        var rows = SetListFilterSort.Apply(searchTrimmed, CategoryRows(_selectedCategoryId), snap);

        _setListOptions.Clear();
        foreach (var set in rows) {
            try {
                _setListOptions.Add(BuildSetListRowData(set, snap));
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

        if (_pendingResetSetScroll) {
            _pendingResetSetScroll = false;
            SetList.ResetScroll();
        }

        if (_pendingSelectSet is { } pendingSet) {
            _pendingSelectSet = null;
            ScrollSetListToSet(pendingSet);
        }
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

    private SetListRowData BuildSetListRowData(GlamourSet set, OwnershipSnapshot snap, bool appendNotInListSuffix = false) {
        var setStorageState = Svc.Get<OwnershipService>().GetSetStorageState(set, snap);
        var subtitle = SetSublineText(set, snap);
        if (appendNotInListSuffix) {
            var searchRaw = _categoryColumn?.Search.Input.String.ToString() ?? string.Empty;
            var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
            if (C.HideSharedModels && !SetListFilterSort.IsVisibleInSetList(set, searchTrimmed, CategoryRows(_selectedCategoryId), snap))
                subtitle += " · Not in list";
        }

        return new SetListRowData {
            Set = set,
            Title = set.Name,
            Subtitle = subtitle,
            IsOwned = snap.OwnedSets.Contains(set),
            IsSelected = ReferenceEquals(_selectedSet, set),
            ShowStorage = setStorageState is SetStorageState.Dresser or SetStorageState.Armoire,
            ShowArmoireWarning = Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(set, snap),
            StorageIconPart = setStorageState == SetStorageState.Armoire ? GlamourIconNode.IconPart.Armoire : GlamourIconNode.IconPart.Dresser,
        };
    }

    private SetListRowData BuildSharedModelItemRowData(uint itemId, OwnershipSnapshot snap) {
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

        var ownedInStorage = snap.StorageOwnedItems.Contains(itemId);
        var ownedAnywhere = snap.OwnedItems.Contains(itemId);
        var subtitle = ownedInStorage ? "Obt. 1/1" : ownedAnywhere ? "In inventory" : "Obt. 0/1";

        ItemStorageState storageState = ItemStorageState.None;
        if (set.NonSetCabinetPiece)
            storageState = Svc.Get<OwnershipService>().GetPieceDisplayStorageState(itemId, set, SetStorageState.None, snap);
        else if (ownedInStorage)
            storageState = snap.ArmoireOwnedItemIds.Contains(itemId) ? ItemStorageState.Armoire
                : snap.DresserItemIds.Contains(set.ItemId) ? ItemStorageState.DresserSet
                : ItemStorageState.DresserLoose;

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
            ShowArmoireWarning = storageState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose && snap.ArmoireCatalogItemIds.Contains(itemId),
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

    private string SetSublineText(GlamourSet set, OwnershipSnapshot snap) {
        var n = set.Items.Count;
        var c = Svc.Get<OwnershipService>().GetOwnedPieceCountForSet(set, snap);
        string core;
        if (set.NonSetCabinetPiece) {
            core = snap.OwnedSets.Contains(set) ? "Obt. 1/1" : $"Obt. {c}/1";
        }
        else if (snap.OwnedSets.Contains(set))
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
