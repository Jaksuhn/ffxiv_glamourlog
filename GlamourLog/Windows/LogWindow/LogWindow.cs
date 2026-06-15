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

    private LogWindowCategoryColumnNode? _categoryColumn;
    private LogWindowSetListColumnNode? _setListColumn;
    private LogWindowDetailColumnNode? _detailColumn;
    private readonly List<SetListRowData> _setListOptions = [];
    private TextNode? _statsSetsLine;
    private TextNode? _statsSpaceLine;
    private VerticalLineNode? _columnSeparatorLeft;
    private VerticalLineNode? _columnSeparatorRight;
    private HorizontalLineNode? _columnSeparatorBottom;
    private CircleButtonNode? _helpMainMenuButton;

    private string _selectedCategoryId = "";
    private GlamourSet? _selectedSet;
    private uint? _sourceFilterPieceItemId;
    private bool _pendingRefreshListsAndDetails;
    private bool _pendingRebuildSetListOrderOnly;
    private bool _pendingPaintDetailsOnly;
    private bool _pendingResetSetScroll;
    private bool _pendingResetDetailScroll;
    private bool _pendingClearSetSelection;
    private GlamourSet? _pendingSelectSet;
    private bool _pendingCategoryPaneRebuild;
    private int _lastDataVersion = -1;
    private readonly List<DetailListRowData> _detailRowOptions = [];
    private readonly HashSet<string> _collapsedDetailSections = [];
    private readonly ContextMenu _contextMenu = new();

    private GlamourSetListNode? SetList => _setListColumn?.List;
    private DetailRowsListNode? DetailList => _detailColumn?.List;

    private const float BottomStatsBlockHeight = 34f;
    private const float FilterCogSize = 28f;
    private const float HelpMenuButtonSize = 28f;

    public LogWindow(FilterWindow filterWindow) {
        _filterWindow = filterWindow;
        _selectedCategoryId = AllCategoryId;
        _lastDataVersion = Svc.Get<CatalogService>().DataVersion;
        DisableClose = C.DisableClose;
    }

    internal void RefreshListsAndDetails() {
        if (!IsOpen || !CanPaintLists())
            return;
        _pendingRefreshListsAndDetails = true;
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

        var alignTop = contentStart.Y + topGap;
        var listBottom = contentStart.Y + contentSize.Y - BottomStatsBlockHeight;
        var midColLeft = contentStart.X + leftWidth + columnGap;
        var setListTop = alignTop + FilterCogSize;
        var setListHeight = listBottom - setListTop;
        var setListRowHeight = GlamourSetListItemNode.ItemHeight;
        var setListVisibleRows = Math.Max(1, (int)(setListHeight / setListRowHeight));
        var setListItemSpacing = Math.Max(0f, setListHeight / setListVisibleRows - setListRowHeight);
        var detailX = contentStart.X + leftWidth + middleWidth + columnGap * 2;
        var detailW = contentSize.X - (leftWidth + middleWidth + columnGap * 2);

        _categoryColumn = new LogWindowCategoryColumnNode(
            leftWidth,
            listBottom - alignTop,
            onSearchChanged: () => {
                _pendingResetSetScroll = true;
                RefreshListsAndDetails();
            },
            onCategorySelected: OnCategorySelected) {
            Position = new Vector2(contentStart.X, alignTop),
        };
        _categoryColumn.AttachNode(this);

        _setListColumn = new LogWindowSetListColumnNode(
            middleWidth,
            setListHeight,
            setListItemSpacing,
            openFilterWindow: () => _filterWindow.OpenOrToggleNear(ComputeFilterWindowScreenOrigin())) {
            Position = new Vector2(midColLeft, alignTop),
        };
        _setListColumn.AttachNode(this);

        _setListColumn.ExportControl.ExportDropDown.OnOptionSelected = OnDataExportFormatSelected;
        _setListColumn.SortControl.SortDropDown.OnOptionSelected = OnSetListSortModeSelected;
        _setListColumn.SortControl.SortDirectionButton.OnClick = OnSetListSortDirectionToggle;
        _setListColumn.SyncSortDirectionChrome();

        SetList!.OnItemSelected = item => {
            if (item is null)
                return;
            if (ReferenceEquals(_selectedSet, item.Set))
                return;
            _selectedSet = item.Set;
            _sourceFilterPieceItemId = null;
            _pendingPaintDetailsOnly = true;
            _pendingResetDetailScroll = true;
        };
        SetList.OnRowRightClick = set => SetContextMenu.Open(this, set, _contextMenu);

        _detailColumn = new LogWindowDetailColumnNode(new Vector2(detailW, listBottom - alignTop)) {
            Position = new Vector2(detailX, alignTop),
        };
        _detailColumn.AttachNode(this);

        DetailList!.OnItemSelected = _ => { };
        DetailList.OnPieceLeftClick = OnDetailPieceItemLeftClick;
        DetailList.OnItemRightClick = id => PieceContextMenu.Open(this, id, _contextMenu);
        DetailList.OnSourceHeaderRightClick = (cfcId, nav) => SourceContextMenu.Open(this, cfcId, nav, _contextMenu);
        DetailList.OnSourceMapFlagLeftClick = (nav, label) => SourceMapFlagger.SetFlagAndOpenMap(nav.TerritoryTypeId, nav.WorldPosition, label);
        DetailList.OnCraftRecipeJournalLeftClick = OnCraftRecipeJournalLeftClick;
        DetailList.OnDetailSectionToggle = OnDetailSectionToggle;
        DetailList.IsDetailSectionCollapsed = title => _collapsedDetailSections.Contains(title);
        DetailList.OnSharedModelSetLeftClick = OnSharedModelSetLeftClick;
        DetailList.OnSharedModelItemLeftClick = OnSharedModelItemLeftClick;
        DetailList.AttachInteractivity();

        var statsWidth = 180f;
        var statsRightX = contentStart.X + contentSize.X - 4f;
        _statsSetsLine = new InventorySpaceCounterNode {
            Position = new Vector2(statsRightX - statsWidth, listBottom + 2f),
            Size = new Vector2(statsWidth, 18f),
        };
        _statsSetsLine.AttachNode(this);

        _statsSpaceLine = new InventorySpaceCounterNode {
            Position = new Vector2(statsRightX - statsWidth, listBottom + 18f),
            Size = new Vector2(statsWidth, 18f),
        };
        _statsSpaceLine.AttachNode(this);

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
        _lastDataVersion = Svc.Get<CatalogService>().DataVersion;

        addon->ShouldFireCallbackAndHideOrClose = C.DisableClose;

        _pendingCategoryPaneRebuild = true;
        if (CanPaintLists())
            _pendingRefreshListsAndDetails = true;
    }

    private void OnCategorySelected(string categoryId) {
        if (_selectedCategoryId == categoryId)
            return;
        _selectedCategoryId = categoryId;
        _selectedSet = null;
        _sourceFilterPieceItemId = null;
        _pendingClearSetSelection = true;
        _pendingRefreshListsAndDetails = true;
        _pendingResetSetScroll = true;
        _pendingResetDetailScroll = true;
    }

    protected override void OnHide(AtkUnitBase* addon) {
        CancelPendingListWork();

        try {
            _categoryColumn?.PrepareForClose();
            _setListColumn?.PrepareForClose();
            _detailColumn?.PrepareForClose();
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

        if (_categoryColumn is null || SetList is null || _statsSetsLine is null || _statsSpaceLine is null || DetailList is null) {
            base.OnUpdate(addon);
            return;
        }

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
                DetailList.ResetScrollToTop();
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
                SetList.ResetScroll();
            }
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] OnUpdate (pre-native)");
        }

        base.OnUpdate(addon);

        try {
            _setListColumn?.SyncRowWidths();
            _detailColumn?.SyncRowWidths();
            _categoryColumn?.SyncCountLayouts();

            SetList.Update();
            DetailList.Update();

            if (!IsOpen) {
                try {
                    _filterWindow.CloseIfOpen();
                }
                catch { }
            }

            _setListColumn?.FilterButton.Icon = _filterWindow.IsOpen ? CircleButtonIcon.ActiveGearCog : CircleButtonIcon.GearCog;
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(LogWindow)}] OnUpdate");
        }
    }

    private bool CanPaintLists()
        => SetList is not null && _statsSetsLine is not null && _statsSpaceLine is not null && _categoryColumn is not null && DetailList is not null;

    protected override void OnFinalize(AtkUnitBase* addon) {
        base.OnFinalize(addon);
        ClearNodeReferences();
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

    private void ClearNodeReferences() {
        _categoryColumn = null;
        _setListColumn = null;
        _detailColumn = null;
        _setListOptions.Clear();
        _detailRowOptions.Clear();
        _collapsedDetailSections.Clear();
        _categoryPaneOrder.Clear();
        _columnSeparatorLeft = null;
        _columnSeparatorRight = null;
        _columnSeparatorBottom = null;
        _statsSetsLine = null;
        _statsSpaceLine = null;
        _helpMainMenuButton = null;
    }

    private Vector2 ComputeFilterWindowScreenOrigin() {
        var unit = (AtkUnitBase*)this;
        var root = unit->RootNode;
        if (root is null)
            return FilterWindow.ClampFilterWindowTopLeft(new Vector2(80f, 80f));

        var mainCenterX = root->X + Size.X * 0.5f;
        var mainCenterY = root->Y + Size.Y * 0.5f;
        var topLeft = new Vector2(mainCenterX - FilterWindow.WindowWidth * 0.5f, mainCenterY - FilterWindow.WindowHeight * 0.5f);
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
