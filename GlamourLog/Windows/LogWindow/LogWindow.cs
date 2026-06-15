using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes;
using GlamourLog.Services;
using GlamourLog.Windows.ContextMenus;
using GlamourLog.Windows.GuideWindow;
using GlamourLog.Windows.LogWindow;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace GlamourLog;

internal unsafe partial class LogWindow : NativeAddon {
    private readonly FilterWindow _filterWindow;
    private readonly List<string> _categoryPaneOrder = [];
    private const string AllCategoryId = "All";

    private ResNode? _midListHeader;
    private SetListExportControlNode? _setListExportControl;
    private SetListSortControlNode? _setListSortControl;
    private CircleButtonNode? _filterSettingsButton;
    private CircleButtonNode? _helpMainMenuButton;
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
    private HorizontalLineNode? _columnSeparatorBottom;
    private ScrollingNode<VerticalListNode>? _categoryListNode;
    private ListNode<SetListRowData, GlamourSetListItemNode>? _setListNode;
    private DetailRowsListNode? _detailRowsListNode; // not ListNode<>: generic ItemData setter can't be overridden; pooled-row ref skip broke piece text (DetailRowsListNode)

    private string _selectedCategoryId = "";
    private GlamourSet? _selectedSet;
    private uint? _sourceFilterPieceItemId;
    private bool _pendingRefreshListsAndDetails;
    private bool _pendingRebuildSetListOrderOnly; // skips left/right columns
    private bool _pendingPaintDetailsOnly;
    private bool _pendingResetSetScroll;
    private bool _pendingResetDetailScroll;
    private bool _pendingClearSetSelection;
    private GlamourSet? _pendingSelectSet;
    private bool _pendingCategoryPaneRebuild; // run Clear before native OnUpdate, not after (uldmgr races dispose post-update)
    private int _lastDataVersion = -1;
    private readonly List<DetailListRowData> _detailRowOptions = [];
    private readonly HashSet<string> _collapsedDetailSections = [];
    private readonly ContextMenu _contextMenu = new();

    private const float BottomStatsBlockHeight = 34f;
    private static readonly Vector4 GatheringHeadingGrey = new(160f / 255f, 160f / 255f, 160f / 255f, 1f);
    private static readonly Vector4 CategoryNameGold = new(216f / 255f, 187f / 255f, 125f / 255f, 1f);
    private const float CategoryHeadingHeight = 26f;
    private const float FilterCogSize = 28f;
    private const float HelpMenuButtonSize = 28f;

    public LogWindow(FilterWindow filterWindow) {
        _filterWindow = filterWindow;
        _selectedCategoryId = AllCategoryId;
        _categoryPaneOrder.AddRange(BuildOrderedCategoryPaneList());
        _lastDataVersion = Svc.Get<CatalogService>().DataVersion;
        DisableClose = C.DisableClose;
    }

    internal void RefreshListsAndDetails() {
        if (!IsOpen || !CanPaintLists())
            return;
        _pendingRefreshListsAndDetails = true;
    }

    private void AttachListCallbacks() {
        _setListExportControl?.ExportDropDown.OnOptionSelected = OnDataExportFormatSelected;
        if (_setListSortControl is not null) {
            _setListSortControl.SortDropDown.OnOptionSelected = OnSetListSortModeSelected;
            _setListSortControl.SortDirectionButton.OnClick = OnSetListSortDirectionToggle;
        }

        _setListNode?.OnItemSelected = item => {
            if (item is null)
                return;
            if (ReferenceEquals(_selectedSet, item.Set))
                return;
            _selectedSet = item.Set;
            _sourceFilterPieceItemId = null;
            _pendingPaintDetailsOnly = true;
            _pendingResetDetailScroll = true;
        };

        GlamourSetListItemNode.OnRowRightClick = set => SetContextMenu.Open(this, set, _contextMenu);
        DetailListItemNode.OnPieceLeftClick = OnDetailPieceItemLeftClick;
        DetailListItemNode.OnItemRightClick = id => PieceContextMenu.Open(this, id, _contextMenu);
        DetailListItemNode.OnSourceHeaderRightClick = (cfcId, nav) => SourceContextMenu.Open(this, cfcId, nav, _contextMenu);
        DetailListItemNode.OnSourceMapFlagLeftClick = (nav, label) => SourceMapFlagger.SetFlagAndOpenMap(nav.TerritoryTypeId, nav.WorldPosition, label);
        DetailListItemNode.OnCraftRecipeJournalLeftClick = OnCraftRecipeJournalLeftClick;
        DetailListItemNode.OnDetailSectionToggle = OnDetailSectionToggle;
        DetailListItemNode.IsDetailSectionCollapsed = title => _collapsedDetailSections.Contains(title);
        DetailListItemNode.OnSharedModelSetLeftClick = OnSharedModelSetLeftClick;
        DetailListItemNode.OnSharedModelItemLeftClick = OnSharedModelItemLeftClick;

        if (_detailRowsListNode is not null) {
            _detailRowsListNode.OnItemSelected = _ => { };
            _detailRowsListNode.AttachInteractivity();
        }
    }

    private void RefreshListsAndDetailsNow() {
        if (!IsOpen || !CanPaintLists())
            return;
        try {
            SyncCategoryPaneToDataVersion();
            PaintListsCore();
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] {nameof(RefreshListsAndDetails)}");
        }
    }

    private void PaintListsCore() {
        // set/detail columns reuse pooled rows; hot-path Clear() disposes atk nodes and races scrollbar (cf. NativeMeters)
        var snap = Svc.Get<OwnershipService>().CaptureSnapshot();
        RefreshRows(snap);
        RefreshDetails(snap);
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        base.OnSetup(addon, atkValueSpan);

        var topGap = 2f;
        var leftWidth = 220f;
        var middleWidth = 320f;
        var columnGap = 4f;
        var contentStart = ContentStartPosition;
        var contentSize = ContentSize;
        var leftPad = 3f;

        _gatheringNoteSearch = new GatheringNoteSearchNode(leftWidth, _ => {
            _pendingResetSetScroll = true;
            RefreshListsAndDetails();
        }) {
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
        var setListTop = alignTop + FilterCogSize;
        var setListHeight = listBottom - setListTop;
        var setListRowHeight = GlamourSetListItemNode.ItemHeight;
        var setListVisibleRows = Math.Max(1, (int)(setListHeight / setListRowHeight));
        var setListItemSpacing = Math.Max(0f, (setListHeight / setListVisibleRows) - setListRowHeight);

        var midHeaderWidth = middleWidth - 8f;
        var midHeaderLeft = midColLeft + 4f;
        var filterRelX = middleWidth - FilterCogSize - leftPad - 4f;

        _midListHeader = new ResNode {
            Position = new Vector2(midHeaderLeft, alignTop),
            Size = new Vector2(midHeaderWidth, FilterCogSize),
        };
        _midListHeader.AttachNode(this);

        var sortRelX = filterRelX - 2f - SetListSortControlNode.LayoutWidth;
        _setListExportControl = new SetListExportControlNode {
            Position = new Vector2(sortRelX - 2f - SetListExportControlNode.LayoutWidth, 0f),
        };
        _setListExportControl.AttachNode(_midListHeader);
        _setListExportControl.ExportDropDown.SelectedOption = GlamourDataExportFormat.LalaAchievements;

        _setListSortControl = new SetListSortControlNode(C.SetListSortDirection) {
            Position = new Vector2(sortRelX, 0f),
        };
        _setListSortControl.AttachNode(_midListHeader);
        _setListSortControl.SortDropDown.SelectedOption = C.SetListSortMode;
        SyncSortDirectionChrome();

        _filterSettingsButton = new CircleButtonNode {
            Icon = CircleButtonIcon.GearCog,
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
            Position = new Vector2(midColLeft, setListTop),
            OptionsList = [],
            AutoResetScroll = false,
            ItemSpacing = setListItemSpacing,
        };
        _setListNode.AttachNode(this);
        _setListNode.Size = new Vector2(middleWidth, setListHeight);
        var detailX = contentStart.X + leftWidth + middleWidth + columnGap * 2;
        var detailW = contentSize.X - (leftWidth + middleWidth + columnGap * 2);
        _detailRowsListNode = new DetailRowsListNode {
            Position = new Vector2(detailX, alignTop),
            OptionsList = [],
        };
        _detailRowsListNode.AttachNode(this);
        _detailRowsListNode.Size = new Vector2(detailW, listBottom - alignTop);

        AttachListCallbacks();

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

        _columnSeparatorBottom = new HorizontalLineNode {
            Position = new Vector2(contentStart.X, listBottom),
            Size = new Vector2(contentSize.X, 2f),
        };
        _columnSeparatorBottom.AttachNode(this);

        var helpBtnY = listBottom + (BottomStatsBlockHeight - HelpMenuButtonSize) * 0.5f;
        _helpMainMenuButton = new CircleButtonNode {
            Icon = CircleButtonIcon.QuestionMark,
            TextTooltip = "Help and tweak settings",
            Size = new Vector2(HelpMenuButtonSize, HelpMenuButtonSize),
            Position = new Vector2(contentStart.X + leftPad, helpBtnY),
            OnClick = () => { Svc.Get<WindowsService>().ToggleMainMenuNearLogWindow(); },
        };
        _helpMainMenuButton.AttachNode(this);

        _categoryPaneOrder.Clear();
        _categoryPaneOrder.AddRange(BuildOrderedCategoryPaneList());
        BuildCategoryButtons();
        _lastDataVersion = Svc.Get<CatalogService>().DataVersion;

        addon->ShouldFireCallbackAndHideOrClose = C.DisableClose; // why is this init-only

        // don't rebuild at all during Setup, just let OnUpdate handle it
        if (CanPaintLists())
            _pendingRefreshListsAndDetails = true;
    }

    protected override void OnHide(AtkUnitBase* addon) {
        CancelPendingListWork();
        ClearStaticCallbacks();
        PrepareScrollbarsForClose();

        try {
            _filterWindow.CloseIfOpen();
            _contextMenu.Close();
            _contextMenu.Clear();
        }
        catch { }
    }

    protected override void OnUpdate(AtkUnitBase* addon) {
        if (!IsOpen) {
            base.OnUpdate(addon);
            return;
        }

        if (_categoryListNode is null || _setListNode is null || _statsSetsLine is null || _statsSpaceLine is null || _detailRowsListNode is null) {
            base.OnUpdate(addon);
            return;
        }

        // only touch ktk lists before base.OnUpdate; after, native graph may have freed nodes from prior frame
        try {
            if (Svc.Get<CatalogService>().TryConsumePendingListRefresh())
                _pendingRefreshListsAndDetails = true;

            if (_pendingCategoryPaneRebuild) {
                _pendingCategoryPaneRebuild = false;
                RebuildCategoryButtonsFromPaneOrder();
                _pendingRefreshListsAndDetails = true;
            }

            if (_pendingResetDetailScroll) {
                _pendingResetDetailScroll = false;
                ResetScrollToTop(_detailRowsListNode);
            }

            if (_pendingRefreshListsAndDetails) {
                _pendingRefreshListsAndDetails = false;
                _pendingPaintDetailsOnly = false;
                _pendingRebuildSetListOrderOnly = false;
                RefreshListsAndDetailsNow();
            }
            else if (_pendingRebuildSetListOrderOnly) {
                _pendingRebuildSetListOrderOnly = false;
                RebuildSetListOrderOnly();
            }

            if (_pendingPaintDetailsOnly) {
                _pendingPaintDetailsOnly = false;
                PaintDetailsOnlyNow();
            }

            if (_pendingResetSetScroll) {
                _pendingResetSetScroll = false;
                _setListNode?.ResetScroll();
            }
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] OnUpdate (pre-native)");
        }

        base.OnUpdate(addon);

        try {
            // ListNode row width uses ScrollBarNode.Bounds during build; sync after native layout.
            SyncVirtualListRowWidths(_setListNode);
            SyncDetailListRowWidths();
            SyncCategoryCountLayouts();

            _setListNode?.Update();
            _detailRowsListNode?.Update();

            if (!IsOpen) {
                try {
                    _filterWindow.CloseIfOpen();
                }
                catch {

                }
            }

            _filterSettingsButton?.Icon = _filterWindow.IsOpen ? CircleButtonIcon.ActiveGearCog : CircleButtonIcon.GearCog;
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] OnUpdate");
        }
    }

    private bool CanPaintLists()
        => _setListNode is not null && _statsSetsLine is not null && _statsSpaceLine is not null && _categoryListNode is not null && _detailRowsListNode is not null;

    private const float VirtualListRowWidthInset = 16f;

    private static void SyncVirtualListRowWidths(ListNode<SetListRowData, GlamourSetListItemNode>? list) {
        if (list is null)
            return;

        var rowWidth = Math.Max(0f, list.Width - VirtualListRowWidthInset);
        foreach (var node in list.OptionNodes) {
            if (Math.Abs(node.Width - rowWidth) > 0.5f)
                node.Width = rowWidth;
        }
    }

    private void SyncDetailListRowWidths()
        => _detailRowsListNode?.SyncRowWidths();

    private static void ResetScrollToTop(ScrollingNode<VerticalListNode>? list) {
        if (list is null)
            return;
        list.ScrollBarNode.ScrollPosition = 0;
        list.RecalculateSizes();
    }

    private static void ResetScrollToTop(ListNode<SetListRowData, GlamourSetListItemNode>? list) {
        if (list is null)
            return;
        list.ResetScroll();
    }

    private static void ResetScrollToTop(DetailRowsListNode? list) {
        if (list is null)
            return;
        // don't FullRebuild here: RefreshDetails already did; second rebuild double-disposes pool nodes
        list.ResetScrollToTop();
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        base.OnFinalize(addon);

        ClearStaticCallbacks();
        ClearNodeReferences();
    }

    public override void Dispose() {
        ClearStaticCallbacks();
        base.Dispose();
    }

    private void CancelPendingListWork() {
        _pendingCategoryPaneRebuild = false;
        _pendingRefreshListsAndDetails = false;
        _pendingRebuildSetListOrderOnly = false;
        _pendingPaintDetailsOnly = false;
        _pendingResetSetScroll = false;
        _pendingResetDetailScroll = false;
        _pendingClearSetSelection = false;
        _pendingSelectSet = null;
    }

    // Scrolled scrollbars keep native drag/position state that races AtkUldManager.Finalizer on close.
    private void PrepareScrollbarsForClose() {
        try {
            if (_setListNode is not null) {
                _setListNode.ScrollBarNode.OnValueChanged = null;
                ResetNativeScrollPosition(_setListNode.ScrollBarNode);
            }
        }
        catch { }

        try {
            _detailRowsListNode?.PrepareForClose();
        }
        catch { }

        try {
            if (_categoryListNode is not null) {
                _categoryListNode.ScrollBarNode.OnValueChanged = null;
                var bar = (AtkComponentScrollBar*)_categoryListNode.ScrollBarNode;
                bar->SetContentNode(null, null);
                ResetNativeScrollPosition(_categoryListNode.ScrollBarNode);
            }
        }
        catch { }
    }

    private static void ResetNativeScrollPosition(ScrollBarNode scrollBar) {
        var bar = (AtkComponentScrollBar*)scrollBar;
        bar->IsBeingDragged = false;
        bar->SetScrollPosition(0);
    }

    private void ClearStaticCallbacks() {
        _setListExportControl?.ExportDropDown.OnOptionSelected = null;
        _setListSortControl?.SortDropDown.OnOptionSelected = null;
        _setListSortControl?.SortDirectionButton.OnClick = null;
        _setListNode?.OnItemSelected = null;
        _detailRowsListNode?.DetachInteractivity();
        GlamourSetListItemNode.OnRowRightClick = null;
        DetailListItemNode.OnPieceLeftClick = null;
        DetailListItemNode.OnItemRightClick = null;
        DetailListItemNode.OnSourceHeaderRightClick = null;
        DetailListItemNode.OnSourceMapFlagLeftClick = null;
        DetailListItemNode.OnCraftRecipeJournalLeftClick = null;
        DetailListItemNode.OnDetailSectionToggle = null;
        DetailListItemNode.IsDetailSectionCollapsed = null;
        DetailListItemNode.OnSharedModelSetLeftClick = null;
        DetailListItemNode.OnSharedModelItemLeftClick = null;
    }

    private void ClearNodeReferences() {
        _midListHeader = null;
        _setListExportControl = null;
        _setListSortControl = null;
        _filterSettingsButton = null;
        _helpMainMenuButton = null;
        _gatheringNoteSearch = null;
        _categoryColumnHeading = null;
        _categoryListNode = null;
        _categoryButtons.Clear();
        _categoryButtonMap.Clear();
        _categoryCountByButton.Clear();
        _setListNode = null;
        _setListOptions.Clear();
        _detailRowOptions.Clear();
        _collapsedDetailSections.Clear();
        _detailRowsListNode = null;
        _columnSeparatorLeft = null;
        _columnSeparatorRight = null;
        _columnSeparatorBottom = null;
        _statsSetsLine = null;
        _statsSpaceLine = null;
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

    internal Vector2 ComputeMainMenuScreenOrigin() {
        var unit = (AtkUnitBase*)this;
        var root = unit->RootNode;
        if (root is null)
            return GuideWindow.ClampTopLeft(new Vector2(80f, 80f));

        var mainCenterX = root->X + Size.X * 0.5f;
        var mainCenterY = root->Y + Size.Y * 0.5f;
        var topLeft = new Vector2(mainCenterX - GuideWindow.WindowWidth * 0.5f, mainCenterY - GuideWindow.WindowHeight * 0.5f);
        return GuideWindow.ClampTopLeft(topLeft);
    }
}
