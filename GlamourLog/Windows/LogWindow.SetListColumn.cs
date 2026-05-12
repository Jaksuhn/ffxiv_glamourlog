using System.ComponentModel;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows;
using KamiToolKit.Nodes;

namespace GlamourLog;

internal unsafe partial class LogWindow {
    private void RefreshRows(HashSet<uint> ownedItems) {
        if (_setListNode is null || _statsSetsLine is null || _statsSpaceLine is null)
            return;

        var agent = ItemFinderModule.Instance();
        if (agent is null) {
            _statsSetsLine.String = "\u2014 / \u2014";
            _statsSpaceLine.String = string.Empty;
            return;
        }

        var ownedSets = Svc.Get<OwnershipService>().GetOwnedSets(ownedItems);

        var totalObtainable = Svc.Get<CatalogService>().GlamourSets.Count(x => !x.IsUnobtainable || ownedSets.Contains(x));
        _statsSetsLine.String = $"{ownedSets.Count} / {totalObtainable}";
        _statsSpaceLine.String = $"{ownedSets.Sum(x => x.Items.Count - 1)}";

        foreach (var btn in _categoryButtons) {
            if (!_categoryButtonMap.TryGetValue(btn, out var categoryId))
                continue;

            btn.LabelNode.String = Svc.Get<CatalogService>().DisplayLabelForCategory(categoryId);
            btn.Selected = categoryId == _selectedCategoryId;
            if (_categoryCountByButton.TryGetValue(btn, out var countNode)) {
                var cr = CategoryRows(categoryId);
                countNode.String = $"{cr.Count(ownedSets.Contains)}/{cr.Count}";
            }
        }
        SyncCategoryCountLayouts();

        RepopulateSetListFromFilteredRows(ownedItems, ownedSets);
    }

    /// <summary> Middle column only: re-filter, re-sort, rebuild row models. Skips stats/category work (sort direction / order changes only). </summary>
    private void RebuildSetListOrderOnly() {
        if (_setListNode is null)
            return;
        if (ItemFinderModule.Instance() is null)
            return;

        var ownedItems = Svc.Get<OwnershipService>().GetOwnedItems();
        var ownedSets = Svc.Get<OwnershipService>().GetOwnedSets(ownedItems);
        RepopulateSetListFromFilteredRows(ownedItems, ownedSets);
    }

    private void RepopulateSetListFromFilteredRows(HashSet<uint> ownedItems, HashSet<GlamourSet> ownedSets) {
        if (_setListNode is null)
            return;

        var searchRaw = _gatheringNoteSearch?.Input.String.ToString() ?? string.Empty;
        var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
        var rows = SetListFilterSort.Apply(searchTrimmed, CategoryRows(_selectedCategoryId), ownedSets, ownedItems);

        if (_selectedSet != null && !rows.Contains(_selectedSet)) {
            _selectedSet = null;
            _pendingClearSetSelection = true;
        }

        _setListOptions.Clear();
        foreach (var set in rows) {
            try {
                var setStorageState = Svc.Get<OwnershipService>().GetSetStorageState(set, ownedItems);
                _setListOptions.Add(new SetListRowData {
                    Set = set,
                    Title = set.Name,
                    Subtitle = SetSublineText(set, ownedSets, ownedItems),
                    IsOwned = ownedSets.Contains(set),
                    ShowStorage = setStorageState is SetStorageState.Dresser or SetStorageState.Armoire,
                    ShowArmoireWarning = Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(set, ownedItems, Svc.Get<CatalogService>().ArmoireItemIds),
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

    private string SetSublineText(GlamourSet set, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        var n = set.Items.Count;
        var c = Svc.Get<OwnershipService>().GetOwnedPieceCountForSet(set, ownedItems);
        string core;
        if (ownedSets.Contains(set))
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
