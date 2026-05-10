using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes;
using GlamourLog.Services;
using KamiToolKit;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace GlamourLog;

internal unsafe class LogWindow : NativeAddon {
    private readonly FilterWindow _filterWindow;
    private readonly List<string> _categoryPaneOrder = [];

    private ResNode? _midListHeader;
    private SetListSortControlNode? _setListSortControl;
    private CircleButtonNode? _filterSettingsButton;
    private GatheringNoteSearchNode? _gatheringNoteSearch;
    private TextNode? _categoryColumnHeading;
    private readonly List<ListButtonNode> _categoryButtons = [];
    private readonly Dictionary<ListButtonNode, TextNode> _categoryCountByButton = [];
    private readonly Dictionary<ListButtonNode, string> _categoryButtonMap = [];
    private readonly List<SetListRowData> _setListOptions = [];
    private TextNode? _statsSetsLine;
    private TextNode? _statsSpaceLine;
    private VerticalLineNode? _columnSeparatorLeft;
    private VerticalLineNode? _columnSeparatorRight;
    private ScrollingListNode? _categoryListNode;
    private ListNode<SetListRowData, GlamourSetListItemNode>? _setListNode;
    private ListNode<DetailListRowData, DetailListItemNode>? _detailRowsListNode;

    private string _selectedCategoryId = "";
    private GlamourSet? _selectedSet;
    private uint? _sourceFilterPieceItemId;
    private bool _isFinalizing;
    private bool _pendingRefreshListsAndDetails;
    private bool _pendingPaintDetailsOnly;
    private bool _pendingResetSetScroll;
    private bool _pendingResetDetailScroll;
    private bool _pendingClearSetSelection;
    private int _lastDataVersion = -1;
    private readonly List<DetailListRowData> _detailRowOptions = [];
    private readonly ContextMenu _contextMenu = new();

    private const float BottomStatsBlockHeight = 34f;
    private static readonly Vector4 GatheringHeadingGrey = new(160f / 255f, 160f / 255f, 160f / 255f, 1f);
    private static readonly Vector4 CategoryNameGold = new(216f / 255f, 187f / 255f, 125f / 255f, 1f);
    private const float CategoryHeadingHeight = 26f;
    private const float FilterCogSize = 28f;

    public LogWindow(FilterWindow filterWindow) {
        _filterWindow = filterWindow;
        _selectedCategoryId = Svc.Get<CatalogService>().UncategorizedTab.Name;
        _categoryPaneOrder.AddRange(BuildOrderedCategoryPaneList());
        _lastDataVersion = Svc.Get<CatalogService>().DataVersion;
        DisableClose = true;
        DisableCloseTransition = true;
    }

    /// <summary> Filter window, inventory hooks, etc.: queue refresh from live game data; applied in OnUpdate. </summary>
    internal void RefreshListsAndDetails() {
        if (_isFinalizing || !IsOpen || !CanPaintLists())
            return;
        _pendingRefreshListsAndDetails = true;
    }

    private void RefreshListsAndDetailsNow() {
        if (_isFinalizing || !IsOpen || !CanPaintLists())
            return;
        try {
            SyncCategoryPaneToDataVersion();
            PaintListsCore();
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] {nameof(RefreshListsAndDetails)}");
        }
    }

    /// <summary> After <see cref="GlamourLog"/> rebuilds catalog rows / grouping (<see cref="GlamourLog.DataVersion"/>). </summary>
    internal void OnBackingDataChanged() => RefreshListsAndDetails();

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

    private void PaintListsCore() {
        // Set/detail columns reuse pooled rows; never ScrollingListNode.Clear() hot paths — that disposes natives
        // and races AtkComponentScrollBar (same pattern as NativeMeters breakdown pooling).
        RefreshRows();
        RefreshDetails(Svc.Get<OwnershipService>().GetOwnedItems());
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        _isFinalizing = false;

        var topGap = 2f;
        var leftWidth = 220f;
        var middleWidth = 320f;
        var columnGap = 4f;
        var contentStart = ContentStartPosition;
        var contentSize = ContentSize;
        var leftPad = 3f;

        _gatheringNoteSearch = new GatheringNoteSearchNode(leftWidth, _ => RefreshListsAndDetails()) {
            Position = new Vector2(contentStart.X, contentStart.Y + topGap),
        };
        _gatheringNoteSearch.AttachNode(this);

        var afterSearchY = contentStart.Y + topGap + _gatheringNoteSearch.Size.Y + 6f;
        _categoryColumnHeading = new TextNode {
            Position = new Vector2(contentStart.X + leftPad, afterSearchY),
            Size = new Vector2(leftWidth - leftPad * 2f, CategoryHeadingHeight),
            FontType = FontType.Jupiter,
            FontSize = 23,
            LineSpacing = 23,
            AlignmentType = AlignmentType.BottomLeft,
            TextColor = GatheringHeadingGrey,
            String = Addon.GetRow(1485).Text,
        };
        _categoryColumnHeading.RemoveTextFlags(TextFlags.Emboss);
        _categoryColumnHeading.AddTextFlags(TextFlags.Emboss);
        _categoryColumnHeading.AttachNode(this);

        var alignTop = contentStart.Y + topGap;
        var listY = afterSearchY + CategoryHeadingHeight + 2f;
        var listBottom = contentStart.Y + contentSize.Y - BottomStatsBlockHeight;
        var listHeight = listBottom - listY;
        var midColLeft = contentStart.X + leftWidth + columnGap;
        var midListTop = alignTop + FilterCogSize + 4f;
        var midListHeight = listBottom - midListTop;

        var midHeaderWidth = middleWidth - 8f;
        var midHeaderLeft = midColLeft + 4f;
        var filterRelX = middleWidth - FilterCogSize - leftPad - 4f;
        var sortRelX = filterRelX - 4f - FilterCogSize;

        _midListHeader = new ResNode {
            Position = new Vector2(midHeaderLeft, alignTop),
            Size = new Vector2(midHeaderWidth, FilterCogSize),
        };
        _midListHeader.AttachNode(this);

        _setListSortControl = new SetListSortControlNode {
            Position = new Vector2(sortRelX, 0f),
        };
        _setListSortControl.AttachNode(_midListHeader);
        _setListSortControl.SortDropDown.SelectedOption = C.SetListSortMode;
        _setListSortControl.SortDropDown.OnOptionSelected = OnSetListSortModeSelected;

        _filterSettingsButton = new CircleButtonNode {
            Icon = ButtonIcon.GearCog,
            TextTooltip = "Set list filters",
            Size = new Vector2(FilterCogSize, FilterCogSize),
            Position = new Vector2(filterRelX, 0f),
            OnClick = () => { _filterWindow.OpenOrToggleNear(ComputeFilterWindowScreenOrigin()); },
        };
        _filterSettingsButton.AttachNode(_midListHeader);

        var statsWidth = 180f;
        var statsRightX = contentStart.X + contentSize.X - 4f;
        _statsSetsLine = new InventorySpaceCounterNode {
            Position = new Vector2(statsRightX - statsWidth, listBottom + 2f),
            Size = new Vector2(statsWidth, 18f)
        };
        _statsSetsLine.AttachNode(this);

        _statsSpaceLine = new InventorySpaceCounterNode {
            Position = new Vector2(statsRightX - statsWidth, listBottom + 18f),
            Size = new Vector2(statsWidth, 18f)
        };
        _statsSpaceLine.AttachNode(this);

        _categoryListNode = SimpleScrollList.Create(new Vector2(contentStart.X, listY), new Vector2(leftWidth, listHeight), true);
        _categoryListNode.AttachNode(this);
        _setListNode = new ListNode<SetListRowData, GlamourSetListItemNode> {
            Position = new Vector2(midColLeft, midListTop),
            OptionsList = [],
            OnItemSelected = item => {
                if (item is null)
                    return;
                if (ReferenceEquals(_selectedSet, item.Set))
                    return;
                _selectedSet = item.Set;
                _sourceFilterPieceItemId = null;
                _pendingPaintDetailsOnly = true;
                _pendingResetDetailScroll = true;
            }
        };
        GlamourSetListItemNode.OnRowRightClick = set => SetContextMenu.Open(this, set, _contextMenu);
        _setListNode.AttachNode(this);
        _setListNode.Size = new Vector2(middleWidth, midListHeight);
        var detailX = contentStart.X + leftWidth + middleWidth + columnGap * 2;
        var detailW = contentSize.X - (leftWidth + middleWidth + columnGap * 2);
        _detailRowsListNode = new ListNode<DetailListRowData, DetailListItemNode> {
            Position = new Vector2(detailX, alignTop),
            OptionsList = [],
            OnItemSelected = _ => { },
        };
        DetailListItemNode.OnPieceLeftClick = OnDetailPieceItemLeftClick;
        DetailListItemNode.OnItemRightClick = id => PieceContextMenu.Open(this, id, _contextMenu);
        DetailListItemNode.OnDutyRightClick = id => SourceContextMenu.Open(this, id, _contextMenu);
        _detailRowsListNode.AttachNode(this);
        _detailRowsListNode.Size = new Vector2(detailW, listBottom - alignTop);

        var sepHalf = 1.5f;
        var sepColumnHeight = listBottom - alignTop;
        _columnSeparatorLeft = new VerticalLineNode {
            Position = new Vector2(contentStart.X + leftWidth + columnGap * 0.5f - sepHalf, alignTop),
            Height = sepColumnHeight,
            Width = 3f,
        };
        _columnSeparatorLeft.AttachNode(this);
        _columnSeparatorRight = new VerticalLineNode {
            Position = new Vector2(contentStart.X + leftWidth + columnGap + middleWidth + columnGap * 0.5f - sepHalf, alignTop),
            Height = sepColumnHeight,
            Width = 3f,
        };
        _columnSeparatorRight.AttachNode(this);

        BuildCategoryButtons();

        base.OnSetup(addon, atkValueSpan);

        // Avoid list teardown/rebuild work during Setup. Perform first sync/paint in OnUpdate pre-native.
        if (!_isFinalizing && CanPaintLists())
            _pendingRefreshListsAndDetails = true;
    }

    protected override void OnUpdate(AtkUnitBase* addon) {
        if (_isFinalizing) {
            base.OnUpdate(addon);
            return;
        }

        if (_categoryListNode is null || _setListNode is null || _statsSetsLine is null || _statsSpaceLine is null || _detailRowsListNode is null) {
            base.OnUpdate(addon);
            return;
        }

        // Mutate scroll lists before NativeAddon.Update runs. If we Clear/reparent after base.OnUpdate,
        // the game's scrollbar code can still be using freed textures/nodes from the prior frame input.
        try {
            if (Svc.Get<CatalogService>().TryConsumePendingListRefresh())
                _pendingRefreshListsAndDetails = true;

            if (_pendingResetSetScroll && _pendingRefreshListsAndDetails && _setListNode is not null) {
                // ListNode keeps an internal scroll index; clearing options first guarantees clamp to zero.
                _setListNode.OptionsList = [];
                _setListNode.FullRebuild();
                _pendingClearSetSelection = true;
                _pendingResetSetScroll = false;
            }

            if (_pendingRefreshListsAndDetails) {
                _pendingRefreshListsAndDetails = false;
                _pendingPaintDetailsOnly = false;
                RefreshListsAndDetailsNow();
            }

            if (_pendingPaintDetailsOnly) {
                _pendingPaintDetailsOnly = false;
                PaintDetailsOnlyNow();
            }

            if (_pendingResetSetScroll) {
                _pendingResetSetScroll = false;
                ResetScrollToTop(_setListNode);
            }

            if (_pendingResetDetailScroll) {
                _pendingResetDetailScroll = false;
                ResetScrollToTop(_detailRowsListNode);
            }
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] OnUpdate (pre-native)");
        }

        base.OnUpdate(addon);

        try {
            _setListNode?.Update();
            _detailRowsListNode?.Update();

            if (!IsOpen) {
                try {
                    _filterWindow.CloseIfOpen();
                }
                catch {

                }
            }

            _filterSettingsButton?.Icon = _filterWindow.IsOpen ? ButtonIcon.ActiveGearCog : ButtonIcon.GearCog;
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] OnUpdate");
        }
    }

    private bool CanPaintLists()
        => _setListNode is not null && _statsSetsLine is not null && _statsSpaceLine is not null && _categoryListNode is not null && _detailRowsListNode is not null;

    /// <summary> After content height collapses or rows are recreated, clamp native scrollbar offset. </summary>
    private static void ResetScrollToTop(ScrollingListNode? list) {
        if (list is null)
            return;
        list.ScrollPosition = 0;
        list.RecalculateLayout();
    }

    private static void ResetScrollToTop(ListNode<SetListRowData, GlamourSetListItemNode>? list) {
        if (list is null)
            return;
        list.ScrollBarNode.ScrollPosition = 0;
        list.FullRebuild();
    }

    private static void ResetScrollToTop(ListNode<DetailListRowData, DetailListItemNode>? list) {
        if (list is null)
            return;
        list.ScrollBarNode.ScrollPosition = 0;
    }

    private void SyncCategoryPaneToDataVersion() {
        if (_categoryListNode is null)
            return;
        if (_lastDataVersion == Svc.Get<CatalogService>().DataVersion)
            return;

        _categoryPaneOrder.Clear();
        _categoryPaneOrder.AddRange(BuildOrderedCategoryPaneList());
        RebuildCategoryButtonsFromPaneOrder();
        if (!_categoryPaneOrder.Contains(_selectedCategoryId))
            _selectedCategoryId = Svc.Get<CatalogService>().UncategorizedTab.Name;
        _lastDataVersion = Svc.Get<CatalogService>().DataVersion;
        ResetScrollToTop(_setListNode);
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        _isFinalizing = true;

        try {
            // Do not Dispose() another NativeAddon from here: Close is safer during AtkUnitBase finalization.
            _filterWindow.CloseIfOpen();
            _gatheringNoteSearch?.Input.ClearFocus();
            _contextMenu.Close();
            _contextMenu.Clear();
        }
        catch { }

        _setListSortControl?.SortDropDown.OnOptionSelected = null;
        _midListHeader = null;
        _setListSortControl = null;
        _filterSettingsButton = null;
        _gatheringNoteSearch = null;
        _categoryColumnHeading = null;
        _categoryListNode = null;
        _categoryButtons.Clear();
        _categoryButtonMap.Clear();
        _categoryCountByButton.Clear();
        _setListNode = null;
        _setListOptions.Clear();
        _detailRowOptions.Clear();
        GlamourSetListItemNode.OnRowRightClick = null;
        DetailListItemNode.OnPieceLeftClick = null;
        DetailListItemNode.OnItemRightClick = null;
        DetailListItemNode.OnDutyRightClick = null;
        _detailRowsListNode = null;
        _columnSeparatorLeft = null;
        _columnSeparatorRight = null;
        _statsSetsLine = null;
        _statsSpaceLine = null;
        base.OnFinalize(addon);
    }

    private Vector2 ComputeFilterWindowScreenOrigin() {
        var unit = (AtkUnitBase*)this;
        var root = unit->RootNode;
        if (root is null)
            return FilterWindow.ClampFilterWindowTopLeft(new Vector2(80f, 80f));

        var mainLeft = root->X;
        var mainTop = root->Y;
        var mainCenterX = mainLeft + Size.X * 0.5f;
        var mainCenterY = mainTop + Size.Y * 0.5f;
        var fw = FilterWindow.WindowWidth;
        var fh = FilterWindow.WindowHeight;
        var topLeft = new Vector2(mainCenterX - fw * 0.5f, mainCenterY - fh * 0.5f);
        return FilterWindow.ClampFilterWindowTopLeft(topLeft);
    }

    private List<string> BuildOrderedCategoryPaneList() {
        var r = new List<string> { Svc.Get<CatalogService>().UncategorizedTab.Name };
        foreach (var (category, _) in Svc.Get<CatalogService>().OutfitCategories.Select((c, ix) => (c, ix)).OrderBy(x => x.c.UiPriority).ThenBy(x => x.ix))
            r.Add(category.Name);
        r.Add(Svc.Get<CatalogService>().UnobtainableTab.Name);
        return r;
    }

    private void BuildCategoryButtons() {
        if (_categoryListNode is null)
            return;
        if (_categoryButtons.Count > 0)
            return;
        RebuildCategoryButtonsFromPaneOrder();
    }

    /// <summary> Rebuilds the category column from <see cref="_categoryPaneOrder"/> (used on first setup and when catalog <see cref="CatalogService.DataVersion"/> changes). </summary>
    private void RebuildCategoryButtonsFromPaneOrder() {
        if (_categoryListNode is null)
            return;

        // Category list is small and only rebuilt when catalog topology changes — full clear is OK (unlike set/detail hot paths).
        _categoryListNode.Clear();
        _categoryButtons.Clear();
        _categoryButtonMap.Clear();
        _categoryCountByButton.Clear();

        foreach (var categoryId in _categoryPaneOrder) {
            var captured = categoryId;
            var button = new ListButtonNode {
                Height = 24f,
                String = string.Empty,
                Selected = captured == _selectedCategoryId,
            };
            button.LabelNode.Position = new Vector2(4f, 1f);
            button.LabelNode.Size = new Vector2(button.Width - 52f, button.Height - 2f);
            button.LabelNode.FontType = FontType.Jupiter;
            button.LabelNode.FontSize = 20;
            button.LabelNode.LineSpacing = 20;
            button.LabelNode.AlignmentType = AlignmentType.Left;
            button.LabelNode.TextColor = CategoryNameGold;
            button.LabelNode.String = Svc.Get<CatalogService>().DisplayLabelForCategory(captured);
            button.LabelNode.AddTextFlags(TextFlags.Emboss, TextFlags.Ellipsis);

            var countNode = new TextNode {
                Position = new Vector2(button.Width - 48f, 1f),
                Size = new Vector2(44f, button.Height - 2f),
                FontType = FontType.Axis,
                FontSize = 11,
                LineSpacing = 11,
                AlignmentType = AlignmentType.BottomRight,
                TextColor = GatheringHeadingGrey,
            };
            countNode.AttachNode(button);

            button.AddEvent(AtkEventType.MouseClick, (_, _, _, _, atkEventData) => {
                if (atkEventData == null)
                    return;
                ref var eventData = ref *atkEventData;
                if (!eventData.IsLeftClick)
                    return;
                if (_selectedCategoryId == captured)
                    return;
                _selectedCategoryId = captured;
                _selectedSet = null;
                _sourceFilterPieceItemId = null;
                _pendingClearSetSelection = true;
                _pendingRefreshListsAndDetails = true;
                _pendingResetSetScroll = true;
                _pendingResetDetailScroll = true;
            });
            _categoryButtons.Add(button);
            _categoryButtonMap[button] = categoryId;
            _categoryCountByButton[button] = countNode;
            _categoryListNode.AddNode(button);
        }

        _categoryListNode.RecalculateLayout();
        SyncCategoryCountLayouts();
        ResetScrollToTop(_categoryListNode);
    }

    private void SyncCategoryCountLayouts() {
        foreach (var btn in _categoryButtons) {
            if (_categoryCountByButton.TryGetValue(btn, out var count)) {
                count.Position = new Vector2(btn.Width - 48f, 1f);
                count.Size = new Vector2(44f, btn.Height - 2f);
                count.AlignmentType = AlignmentType.BottomRight;
            }
        }
    }

    private List<GlamourSet> CategoryRows(string categoryId)
        => Svc.Get<CatalogService>().GlamourSetsByCategory.TryGetValue(categoryId, out var list) ? list : [];

    private void RefreshRows() {
        if (_setListNode is null || _statsSetsLine is null || _statsSpaceLine is null)
            return;

        var agent = ItemFinderModule.Instance();
        if (agent is null) {
            _statsSetsLine.String = "\u2014 / \u2014";
            _statsSpaceLine.String = string.Empty;
            return;
        }

        var ownedItems = Svc.Get<OwnershipService>().GetOwnedItems();
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

        var rows = GetFilteredRows(ownedSets, ownedItems);

        if (_selectedSet != null && !rows.Contains(_selectedSet)) {
            _selectedSet = null;
            _pendingClearSetSelection = true;
        }

        _setListOptions.Clear();
        foreach (var set in rows) {
            try {
                var setStorageState = Svc.Get<OwnershipService>().GetSetStorageState(set, ownedItems);
                var showStorage = setStorageState is SetStorageState.Dresser or SetStorageState.Armoire;
                var showArmoireWarning = Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(set, ownedItems, Svc.Get<CatalogService>().ArmoireItemIds);
                _setListOptions.Add(new SetListRowData {
                    Set = set,
                    Title = set.Name,
                    Subtitle = SetSublineText(set, ownedSets, ownedItems),
                    IsOwned = ownedSets.Contains(set),
                    ShowStorage = showStorage,
                    ShowArmoireWarning = showArmoireWarning,
                    StorageIconPart = setStorageState == SetStorageState.Armoire
                        ? GlamourIconNode.IconPart.Armoire
                        : GlamourIconNode.IconPart.Dresser,
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

    private List<GlamourSet> GetFilteredRows(HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        var searchRaw = _gatheringNoteSearch?.Input.String.ToString() ?? string.Empty;
        var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
        var rows = searchTrimmed.Length > 0 ? [.. Svc.Get<CatalogService>().GlamourSets] : CategoryRows(_selectedCategoryId);

        if (C.HideCompleted)
            rows = [.. rows.Where(r => !ownedSets.Contains(r))];

        var hasPositiveFilters = C.HideNonPartials || C.HideUnaffordable || C.HideUnready || C.HideNoMarketboard;
        if (hasPositiveFilters) {
            var inventoryOnly = C.HideUnready ? Svc.Get<OwnershipService>().GetInventoryItemsOnly() : null;
            rows = [.. rows.Where(r =>
                (!C.HideNonPartials || Svc.Get<OwnershipService>().IsPartiallyCompleted(r, ownedSets, ownedItems)) &&
                (!C.HideUnaffordable || Svc.Get<OwnershipService>().CanAffordAllMissingGearPieces(r, ownedItems)) &&
                (!C.HideUnready || (inventoryOnly is not null && OwnershipService.SetHasPieceInPlayerInventory(r, inventoryOnly))) &&
                (!C.HideNoMarketboard || Svc.Get<OwnershipService>().IsMarketboardPurchasable(r))
            )];
        }

        if (C.ShowOnlyMisplaced)
            rows = [.. rows.Where(r => Svc.Get<OwnershipService>().SetHasArmoireMisplacementWarning(r, ownedItems, Svc.Get<CatalogService>().ArmoireItemIds))];

        if (searchTrimmed.Length > 0)
            rows = [.. rows.Where(r => SetMatchesSearch(r, searchTrimmed))];

        return ApplySetListSort(rows);
    }

    private void OnSetListSortModeSelected(GlamourSetSortMode mode) {
        if (C.SetListSortMode == mode)
            return;
        C.SetListSortMode = mode;
        C.Save();
        RefreshListsAndDetails();
    }

    private static List<GlamourSet> ApplySetListSort(List<GlamourSet> rows) {
        var mode = C.SetListSortMode;
        return mode switch {
            GlamourSetSortMode.AlphabeticalAscending => [.. rows.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            GlamourSetSortMode.ItemLevelDescending => [.. rows.OrderByDescending(s => s.SortItemLevel).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            GlamourSetSortMode.PatchDescending => [.. rows.OrderByDescending(s => s.SortPatchNo).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.ItemId)],
            _ => rows,
        };
    }

    private static bool SetMatchesSearch(GlamourSet set, string query) {
        var t = query.Trim();
        return set.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || set.Items.Any(id => Item.GetRowRef(id) is { IsValid: true, Value.Name: var name } && name.ToString().Contains(t, StringComparison.OrdinalIgnoreCase));
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
            return;
        }

        var setJournalLine = string.IsNullOrWhiteSpace(_selectedSet.Name)
            ? Item.GetRow(_selectedSet.ItemId).Name.ToString()
            : _selectedSet.Name;
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
                ShowArmoireWarning =
                    storageState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose
                    && Svc.Get<CatalogService>().ArmoireItemIds.Contains(itemId),
            });
        }

        if (items.Count > 0 && TryGetCostTotals(_selectedSet, _sourceFilterPieceItemId, out var costTotals)) {
            _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Costs" });
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.JournalHeader,
                PrimaryText = _sourceFilterPieceItemId is not null
                    ? "Currencies Required (Single Item)"
                    : "Currencies Required (Full Set)"
            });
            var ordered = costTotals.OrderBy(x => Item.GetRow(x.Key).Name.ToString(), StringComparer.Ordinal).ToList();
            foreach (var kv in ordered) {
                var owned = GetOwnedCurrencyCount(kv.Key);
                _detailRowOptions.Add(new DetailListRowData {
                    Kind = DetailRowKind.Cost,
                    ItemId = kv.Key,
                    PrimaryText = Item.GetRow(kv.Key).Name.ToString(),
                    SecondaryText = $"Obt. {owned}/{kv.Value}",
                });
            }
        }

        _detailRowOptions.Add(new DetailListRowData { Kind = DetailRowKind.SectionHeader, PrimaryText = "Sources" });
        var hierarchy = Svc.Get<CatalogService>().GetDutyChestSourceHierarchy(_selectedSet, _sourceFilterPieceItemId);
        if (hierarchy.Count == 0) {
            _detailRowOptions.Add(new DetailListRowData {
                Kind = DetailRowKind.EmptyHint,
                PrimaryText = Addon.GetRow(5494).Text.ToString()
            });
        }
        else {
            foreach (var g in hierarchy) {
                var nonEmptyChests = g.ChestRows.Where(x => x.ItemIds.Count > 0).ToList();
                if (nonEmptyChests.Count == 0)
                    continue;

                _detailRowOptions.Add(new DetailListRowData {
                    Kind = DetailRowKind.SourceDuty,
                    PrimaryText = g.DutyName,
                    ContentFinderConditionId = g.ContentFinderConditionId,
                });

                foreach (var chest in nonEmptyChests) {
                    _detailRowOptions.Add(new DetailListRowData {
                        Kind = DetailRowKind.SourceChest,
                        PrimaryText = FormatChestSourceLabel(chest),
                        SourceItemIds = chest.ItemIds,
                    });
                }
            }
        }
        _detailRowsListNode.OptionsList = [.. _detailRowOptions];
    }

    private static string FormatChestSourceLabel(ChestSourceRow chest)
        => chest.TerritoryTypeId == uint.MaxValue ? "FATE" : (chest.ChestNo != 0 ? $"Chest {chest.ChestNo}" : "Chest");

    private void OnDetailPieceItemLeftClick(uint itemId) {
        if (_isFinalizing)
            return;
        _sourceFilterPieceItemId = _sourceFilterPieceItemId == itemId ? null : itemId;
        _pendingPaintDetailsOnly = true;
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
        return CurrencyManager.Instance()->SpecialItemBucket.TryGetValue(costItemId, out var value, true)
            ? (int)value.Count
            : InventoryManager.Instance()->GetInventoryItemCount(costItemId);
    }

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

        string? sortHint = C.SetListSortMode switch {
            GlamourSetSortMode.PatchDescending => set.SortPatchNo == 0m
                ? "Patch —"
                : $"Patch {set.SortPatchNo.ToString(CultureInfo.InvariantCulture)}",
            GlamourSetSortMode.ItemLevelDescending => set.SortItemLevel == 0
                ? "iLvl —"
                : $"iLvl {set.SortItemLevel}",
            _ => null,
        };
        return sortHint is null ? core : $"{core} · {sortHint}";
    }

    private static GlamourIconNode.IconPart? StorageIconPartFor(ItemStorageState storageState)
        => storageState switch {
            ItemStorageState.Armoire => GlamourIconNode.IconPart.Armoire,
            ItemStorageState.DresserLoose => GlamourIconNode.IconPart.DresserFaded,
            ItemStorageState.DresserSet => GlamourIconNode.IconPart.Dresser,
            _ => null,
        };

    private ItemStorageState ResolvePieceStorageState(uint itemId, SetStorageState setStorageState)
        => Svc.Get<OwnershipService>().GetPieceDisplayStorageState(itemId, _selectedSet!, setStorageState);
}
