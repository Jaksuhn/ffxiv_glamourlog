using System.ComponentModel;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows.LogWindow;
using KamiToolKit.Nodes;

namespace GlamourLog;

internal unsafe partial class LogWindow {
    private void RefreshRows(OwnershipSnapshot snap) {
        if (_setListNode is null || _statsSetsLine is null || _statsSpaceLine is null)
            return;

        var agent = ItemFinderModule.Instance();
        if (agent is null) {
            _statsSetsLine.String = "\u2014 / \u2014";
            _statsSpaceLine.String = string.Empty;
            return;
        }

        // mirage sets only. non-set armoire doesn't get included
        var mirageCatalogSets = Svc.Get<CatalogService>().GlamourSets.Where(s => !s.NonSetCabinetPiece);
        var ownedMirageSets = snap.OwnedSets.Where(s => !s.NonSetCabinetPiece);
        var totalObtainable = mirageCatalogSets.Count(x => !x.IsUnobtainable || snap.OwnedSets.Contains(x));
        _statsSetsLine.String = $"{ownedMirageSets.Count()} / {totalObtainable}";
        _statsSpaceLine.String = $"{ownedMirageSets.Sum(x => x.Items.Count - 1)}";

        foreach (var btn in _categoryButtons) {
            if (!_categoryButtonMap.TryGetValue(btn, out var categoryId))
                continue;

            btn.LabelNode.String = Svc.Get<CatalogService>().DisplayLabelForCategory(categoryId);
            btn.Selected = categoryId == _selectedCategoryId;
            if (_categoryCountByButton.TryGetValue(btn, out var countNode)) {
                var cr = CategoryRows(categoryId);
                countNode.String = $"{cr.Count(snap.OwnedSets.Contains)}/{cr.Count}";
            }
        }
        SyncCategoryCountLayouts();

        RepopulateSetListFromFilteredRows(snap);
    }

    // middle column only: re-sort / re-filter row models; skips category + stats (sort chrome toggles)
    private void RebuildSetListOrderOnly() {
        if (_setListNode is null)
            return;
        if (ItemFinderModule.Instance() is null)
            return;

        RepopulateSetListFromFilteredRows(Svc.Get<OwnershipService>().CaptureSnapshot());
    }

    private void RepopulateSetListFromFilteredRows(OwnershipSnapshot snap) {
        if (_setListNode is null)
            return;

        var searchRaw = _gatheringNoteSearch?.Input.String.ToString() ?? string.Empty;
        var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
        var rows = SetListFilterSort.Apply(searchTrimmed, CategoryRows(_selectedCategoryId), snap);

        if (_selectedSet != null && !rows.Contains(_selectedSet)) {
            _selectedSet = null;
            _pendingClearSetSelection = true;
        }

        _setListOptions.Clear();
        foreach (var set in rows) {
            try {
                var setStorageState = Svc.Get<OwnershipService>().GetSetStorageState(set, snap);
                _setListOptions.Add(new SetListRowData {
                    Set = set,
                    Title = set.Name,
                    Subtitle = SetSublineText(set, snap),
                    IsOwned = snap.OwnedSets.Contains(set),
                    ShowStorage = setStorageState is SetStorageState.Dresser or SetStorageState.Armoire,
                    ShowArmoireWarning = Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(set, snap),
                    StorageIconPart = setStorageState == SetStorageState.Armoire ? GlamourIconNode.IconPart.Armoire : GlamourIconNode.IconPart.Dresser,
                });
            }
            catch (Exception ex) {
                Svc.Log.Error(ex, $"[{nameof(LogWindow)}] Build virtual set row failed");
            }
        }

        _setListNode.OptionsList = [.. _setListOptions];
        if (_pendingClearSetSelection) {
            _pendingClearSetSelection = false;
            // ktk ListNode: rebuild pool so internal scroll + selection can't reference wrong row after options clear
            _setListNode.FullRebuild();
        }
    }

    private void OnSetListSortModeSelected(GlamourSetSortMode mode) {
        if (C.SetListSortMode == mode)
            return;
        C.SetListSortMode = mode;
        C.SetListSortDirection = mode.DefaultDirection();
        C.Save();
        SyncSortDirectionChrome();
        RefreshListsAndDetails();
    }

    private void OnSetListSortDirectionToggle() {
        C.SetListSortDirection = C.SetListSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        C.Save();
        SyncSortDirectionChrome();
        if (_isFinalizing || !IsOpen || !CanPaintLists())
            return;
        _pendingRebuildSetListOrderOnly = true;
    }

    private void SyncSortDirectionChrome() {
        if (_setListSortControl is null)
            return;
        var btn = _setListSortControl.SortDirectionButton;
        btn.Icon = SortDirectionButtonIcon(C.SetListSortDirection);
        btn.TextTooltip = C.SetListSortDirection == ListSortDirection.Ascending ? Addon.GetRow(8043).Text : Addon.GetRow(8044).Text;
    }

    private static ButtonIcon SortDirectionButtonIcon(ListSortDirection direction)
        => direction == ListSortDirection.Ascending ? ButtonIcon.UpArrow : ButtonIcon.ArrowDown;

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
