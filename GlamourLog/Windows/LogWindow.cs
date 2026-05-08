using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes;
using GlamourLog.Services;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog;

internal unsafe class LogWindow : NativeAddon {
    private readonly FilterWindow _filterWindow;
    private readonly List<string> _categoryPaneOrder = [];

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
    private ScrollingListNode? _detailListNode;

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
    private bool _detailPanelInitialized;
    private TreeComboSectionNode? _detailEmptySection;
    private TextNode? _detailEmptyHint;
    private TreeComboSectionNode? _detailPiecesSection;
    private TreeComboSectionNode? _detailSourcesSection;
    private TextNode? _detailSourcesEmptyLine;
    private TreeComboSectionNode? _detailCostsSection;
    private readonly List<TreeListHeaderNode> _sourceDutyHeaderPool = [];
    private readonly Dictionary<TreeListHeaderNode, uint> _sourceDutyFinderCfcByHeader = [];
    private readonly List<PooledSourceChestRow> _sourceChestRowPool = [];
    private readonly List<PooledDetailPieceRow> _detailPieceRowPool = [];
    private readonly Dictionary<ListButtonNode, uint> _detailPieceItemByRow = [];
    private readonly List<PooledDetailCostRow> _detailCostRowPool = [];
    private readonly Dictionary<ListButtonNode, uint> _detailCostCurrencyByRow = [];
    private readonly KamiToolKit.ContextMenu.ContextMenu _contextMenu = new();

    private sealed class PooledSourceChestRow {
        public SimpleComponentNode Row = null!;
        public TextNode Label = null!;
        public readonly List<FramedItemIconNode> Icons = [];
    }

    private sealed class PooledDetailPieceRow {
        public ListButtonNode Button = null!;
        public FramedItemIconNode Icon = null!;
        public TextNode Status = null!;
        public GlamourIconNode StorageBadge = null!;
        public GlamourIconNode.IconPart LastStorageIconPart;
    }

    private sealed class PooledDetailCostRow {
        public GatheringNoteItemNode Row = null!;
    }

    private const float ListRowHeight = 28f;
    private const float SetListRowHeight = 38f;
    private static readonly Vector4 SetTitleWhite = new(1f, 1f, 1f, 1f);
    private const float ListIconSize = 22f;
    private const float ListIconPadX = 2f;
    private const float ListIconPadY = 2f;
    private const float DetailLabelLeft = 32f;
    private const float DetailStatusWidth = 40f;
    private const float CostListRowHeight = 40f;
    private const float SourceChestRowHeight = 26f;
    private const float SourceChestLabelWidth = 168f;
    private const float SourceLootIconSize = 22f;
    private const float SourceLootIconGap = 2f;
    private const float BottomStatsBlockHeight = 34f;
    private static readonly Vector4 ItemStatusGrey = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 GatheringHeadingGrey = new(160f / 255f, 160f / 255f, 160f / 255f, 1f);
    private static readonly Vector4 CategoryNameGold = new(216f / 255f, 187f / 255f, 125f / 255f, 1f);
    private const float CategoryHeadingHeight = 26f;
    private const float FilterCogSize = 28f;

    public LogWindow(FilterWindow filterWindow) {
        _filterWindow = filterWindow;
        _selectedCategoryId = Svc.Catalog.UncategorizedTab.Name;
        _categoryPaneOrder.AddRange(BuildOrderedCategoryPaneList());
        _lastDataVersion = Svc.Catalog.DataVersion;
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
            Svc.PluginLog.Error(ex, $"[{nameof(LogWindow)}] {nameof(RefreshListsAndDetails)}");
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
            RefreshDetails(Svc.Ownership.GetOwnedItems());
        }
        catch (Exception ex) {
            Svc.PluginLog.Error(ex, $"[{nameof(LogWindow)}] {nameof(PaintDetailsOnly)}");
        }
    }

    private void PaintListsCore() {
        // Set/detail columns reuse pooled rows; never ScrollingListNode.Clear() hot paths — that disposes natives
        // and races AtkComponentScrollBar (same pattern as NativeMeters breakdown pooling).
        RefreshRows();
        RefreshDetails(Svc.Ownership.GetOwnedItems());
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        _isFinalizing = false;

        var topGap = 2f;
        var leftWidth = 190f;
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

        _filterSettingsButton = new CircleButtonNode {
            Icon = ButtonIcon.GearCog,
            TextTooltip = "Set list filters",
            Size = new Vector2(FilterCogSize, FilterCogSize),
            Position = new Vector2(midColLeft + middleWidth - FilterCogSize - leftPad, alignTop),
            OnClick = () => { _filterWindow.OpenOrToggleNear(ComputeFilterWindowScreenOrigin()); },
        };
        _filterSettingsButton.AttachNode(this);

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
            Size = new Vector2(middleWidth, midListHeight),
            ItemSpacing = 0f,
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
            },
        };
        GlamourSetListItemNode.OnRowRightClick = OpenSetContextMenu;
        _setListNode.AttachNode(this);
        var detailX = contentStart.X + leftWidth + middleWidth + columnGap * 2;
        var detailW = contentSize.X - (leftWidth + middleWidth + columnGap * 2);
        _detailListNode = SimpleScrollList.Create(new Vector2(detailX, alignTop), new Vector2(detailW, listBottom - alignTop), false);
        _detailListNode.AttachNode(this);

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

        SyncCategoryPaneToDataVersion();
        if (!_isFinalizing && CanPaintLists()) {
            try {
                PaintListsCore();
            }
            catch (Exception ex) {
                Svc.PluginLog.Error(ex, $"[{nameof(LogWindow)}] OnSetup paint");
            }
        }
    }

    protected override void OnUpdate(AtkUnitBase* addon) {
        if (_isFinalizing) {
            base.OnUpdate(addon);
            return;
        }

        if (_categoryListNode is null || _setListNode is null || _statsSetsLine is null || _statsSpaceLine is null || _detailListNode is null) {
            base.OnUpdate(addon);
            return;
        }

        // Mutate scroll lists before NativeAddon.Update runs. If we Clear/reparent after base.OnUpdate,
        // the game's scrollbar code can still be using freed textures/nodes from the prior frame input.
        try {
            if (Svc.Catalog.TryConsumePendingListRefresh())
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
                ResetScrollToTop(_detailListNode);
            }
        }
        catch (Exception ex) {
            Svc.PluginLog.Error(ex, $"[{nameof(LogWindow)}] OnUpdate (pre-native)");
        }

        base.OnUpdate(addon);

        try {
            _setListNode?.Update();

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
            Svc.PluginLog.Error(ex, $"[{nameof(LogWindow)}] OnUpdate");
        }
    }

    private bool CanPaintLists()
        => _setListNode is not null && _statsSetsLine is not null && _statsSpaceLine is not null && _categoryListNode is not null && _detailListNode is not null;

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

    private void SyncCategoryPaneToDataVersion() {
        if (_categoryListNode is null)
            return;
        if (_lastDataVersion == Svc.Catalog.DataVersion)
            return;

        _selectedCategoryId = MigrateLegacyCategoryKey(_selectedCategoryId);
        _categoryPaneOrder.Clear();
        _categoryPaneOrder.AddRange(BuildOrderedCategoryPaneList());
        BuildCategoryButtons();
        if (!_categoryPaneOrder.Contains(_selectedCategoryId))
            _selectedCategoryId = Svc.Catalog.UncategorizedTab.Name;
        _lastDataVersion = Svc.Catalog.DataVersion;
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

        _filterSettingsButton = null;
        _gatheringNoteSearch = null;
        _categoryColumnHeading = null;
        _categoryListNode = null;
        _categoryButtons.Clear();
        _categoryButtonMap.Clear();
        _categoryCountByButton.Clear();
        _setListNode = null;
        _setListOptions.Clear();
        _detailPanelInitialized = false;
        _detailEmptySection = null;
        _detailEmptyHint = null;
        _detailPiecesSection = null;
        _detailSourcesSection = null;
        _detailSourcesEmptyLine = null;
        _sourceDutyHeaderPool.Clear();
        _sourceChestRowPool.Clear();
        _detailCostsSection = null;
        _detailPieceRowPool.Clear();
        _detailPieceItemByRow.Clear();
        _detailCostRowPool.Clear();
        _detailCostCurrencyByRow.Clear();
        GlamourSetListItemNode.OnRowRightClick = null;
        _detailListNode = null;
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

    // TODO: don't need this?
    private static string MigrateLegacyCategoryKey(string key) => key switch {
        "__uncategorized" => "Unsorted",
        "__unobtainable" => "Unobtainable",
        _ => key,
    };

    private List<string> BuildOrderedCategoryPaneList() {
        var r = new List<string> { Svc.Catalog.UncategorizedTab.Name };
        foreach (var (category, _) in Svc.Catalog.OutfitCategories.Select((c, ix) => (c, ix)).OrderBy(x => x.c.UiPriority).ThenBy(x => x.ix))
            r.Add(category.Name);
        r.Add(Svc.Catalog.UnobtainableTab.Name);
        return r;
    }

    private void BuildCategoryButtons() {
        if (_categoryListNode is null)
            return;

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
            button.LabelNode.String = Svc.Catalog.DisplayLabelForCategory(captured);
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
        => Svc.Catalog.GlamourSetsByCategory.TryGetValue(categoryId, out var list) ? list : [];

    private void RefreshRows() {
        if (_setListNode is null || _statsSetsLine is null || _statsSpaceLine is null)
            return;

        var agent = ItemFinderModule.Instance();
        if (agent is null) {
            _statsSetsLine.String = "\u2014 / \u2014";
            _statsSpaceLine.String = string.Empty;
            return;
        }

        var ownedItems = Svc.Ownership.GetOwnedItems();
        var ownedSets = Svc.Ownership.GetOwnedSets(ownedItems);

        var totalObtainable = Svc.Catalog.GlamourSets.Count(x => !x.IsUnobtainable || ownedSets.Contains(x));
        _statsSetsLine.String = $"{ownedSets.Count} / {totalObtainable}";
        _statsSpaceLine.String = $"{ownedSets.Sum(x => x.Items.Count - 1)}";

        foreach (var btn in _categoryButtons) {
            if (!_categoryButtonMap.TryGetValue(btn, out var categoryId))
                continue;

            btn.LabelNode.String = Svc.Catalog.DisplayLabelForCategory(categoryId);
            btn.Selected = categoryId == _selectedCategoryId;
            if (_categoryCountByButton.TryGetValue(btn, out var countNode)) {
                var cr = CategoryRows(categoryId);
                countNode.String = $"{cr.Count(ownedSets.Contains)}/{cr.Count}";
            }
        }
        SyncCategoryCountLayouts();

        var config = Svc.Config;

        var rows = GetFilteredRows(config, ownedSets, ownedItems);

        if (_selectedSet != null && !rows.Contains(_selectedSet)) {
            _selectedSet = null;
            _pendingClearSetSelection = true;
        }

        _setListOptions.Clear();
        foreach (var set in rows) {
            try {
                var setStorageState = Svc.Ownership.GetSetStorageState(set, ownedItems);
                var showStorage = setStorageState is SetStorageState.Dresser or SetStorageState.Armoire;
                _setListOptions.Add(new SetListRowData {
                    Set = set,
                    Title = set.Name,
                    Subtitle = SetSublineText(set, ownedSets, ownedItems),
                    IsOwned = ownedSets.Contains(set),
                    ShowStorage = showStorage,
                    StorageIconPart = setStorageState == SetStorageState.Armoire
                        ? GlamourIconNode.IconPart.Armoire
                        : GlamourIconNode.IconPart.Dresser,
                });
            }
            catch (Exception ex) {
                Svc.PluginLog.Error(ex, $"[{nameof(LogWindow)}] Build virtual set row failed");
            }
        }

        _setListNode.OptionsList = [.. _setListOptions];
        if (_pendingClearSetSelection) {
            _pendingClearSetSelection = false;
            _setListNode.FullRebuild();
        }
    }

    private List<GlamourSet> GetFilteredRows(Configuration? config, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        var searchRaw = _gatheringNoteSearch?.Input.String.ToString() ?? string.Empty;
        var searchTrimmed = string.IsNullOrWhiteSpace(searchRaw) ? string.Empty : searchRaw.Trim();
        var rows = searchTrimmed.Length > 0 ? [.. Svc.Catalog.GlamourSets] : CategoryRows(_selectedCategoryId);

        if (config != null) {
            if (config.HideCompleted)
                rows = [.. rows.Where(r => !ownedSets.Contains(r))];

            var hasPositiveFilters = config.HideNonPartials || config.HideUnaffordable || config.HideUnready || config.HideNoMarketboard;
            if (hasPositiveFilters) {
                rows = [.. rows.Where(r =>
                    (config.HideNonPartials && Svc.Ownership.IsPartiallyCompleted(r, ownedSets, ownedItems)) ||
                    (config.HideUnaffordable && Svc.Ownership.CanAffordAllMissingGearPieces(r, ownedItems)) ||
                    (config.HideUnready && Svc.Ownership.IsDoneButNotInDresser(r, ownedSets, ownedItems)) ||
                    (config.HideNoMarketboard && Svc.Ownership.IsMarketboardPurchasable(r))
                )];
            }
        }

        if (searchTrimmed.Length > 0)
            rows = [.. rows.Where(r => SetMatchesSearch(r, searchTrimmed))];

        return rows;
    }

    private static bool SetMatchesSearch(GlamourSet set, string query) {
        var t = query.Trim();
        return set.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || set.Items.Any(id => Item.GetRowRef(id) is { IsValid: true, Value.Name: var name } && name.ToString().Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshDetails(HashSet<uint> ownedItems) {
        if (_detailListNode is null)
            return;

        if (_selectedSet == null)
            _sourceFilterPieceItemId = null;

        var listWidth = _detailListNode.Size.X > 8f ? _detailListNode.Size.X : 280f;
        EnsureDetailPanelShell(listWidth);
        SyncDetailSectionWidths(listWidth);
        ResetSourcesUiRows();
        ResetCostsUiRows();

        var inventoryItems = Svc.Ownership.GetInventoryItemsOnly();

        if (_selectedSet == null) {
            _detailEmptySection!.IsVisible = true;
            _detailPiecesSection!.IsVisible = false;
            _detailSourcesSection!.IsVisible = false;
            _detailCostsSection!.IsVisible = false;
            _detailEmptyHint!.String = "Select a set from the list to view pieces.";
            if (_detailPiecesSection is { } ps)
                ps.SetJournal("No set selected");

            foreach (var h in _detailPieceRowPool)
                h.Button.IsVisible = false;
            _detailPieceItemByRow.Clear();

            RecalculateDetailPanels();
            return;
        }

        _detailEmptySection!.IsVisible = false;
        _detailPiecesSection!.IsVisible = true;
        _detailSourcesSection!.IsVisible = true;
        _detailPiecesSection.String = "Set Details";
        var setJournalLine = string.IsNullOrWhiteSpace(_selectedSet.Name) ? Item.GetRow(_selectedSet.ItemId).Name.ToString() : _selectedSet.Name;
        _detailPiecesSection.SetJournal(setJournalLine);

        _detailPieceItemByRow.Clear();
        var items = _selectedSet.Items;
        for (var i = 0; i < items.Count; i++) {
            while (_detailPieceRowPool.Count <= i)
                CreatePooledPieceRow(listWidth);

            var h = _detailPieceRowPool[i];
            BindDetailPieceRow(h, listWidth, items[i], ownedItems, inventoryItems);
            h.Button.IsVisible = true;
        }

        for (var i = items.Count; i < _detailPieceRowPool.Count; i++)
            _detailPieceRowPool[i].Button.IsVisible = false;

        if (items.Count > 0 && TryGetCostTotals(_selectedSet, _sourceFilterPieceItemId, out var costTotals)) {
            _detailCostsSection!.IsVisible = true;
            if (_detailCostsSection.JournalHeader is { } costsJournal) {
                costsJournal.String = _sourceFilterPieceItemId is not null
                    ? "Currencies Required (Single Item)"
                    : "Currencies Required (Full Set)";
            }
            _detailCostsSection.String = "Costs";

            _detailCostCurrencyByRow.Clear();
            var ordered = costTotals.OrderBy(x => Item.GetRow(x.Key).Name.ToString(), StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++) {
                while (_detailCostRowPool.Count <= i)
                    CreatePooledCostRow(listWidth);
                var h = _detailCostRowPool[i];
                var kv = ordered[i];
                BindCostRow(h, listWidth, kv.Key, kv.Value);
                h.Row.IsVisible = true;
            }
            for (var i = ordered.Count; i < _detailCostRowPool.Count; i++)
                _detailCostRowPool[i].Row.IsVisible = false;
        }
        else {
            _detailCostsSection!.IsVisible = false;
        }

        _detailSourcesSection!.String = "Sources";
        var hierarchy = Svc.Catalog.GetDutyChestSourceHierarchy(_selectedSet, _sourceFilterPieceItemId);

        if (hierarchy.Count == 0) {
            if (_detailSourcesEmptyLine is not null) {
                _detailSourcesEmptyLine.String = Addon.GetRow(5494).Text; // No resuls found.
                _detailSourcesEmptyLine.TextColor = ItemStatusGrey;
                _detailSourcesEmptyLine.IsVisible = true;
            }
        }
        else {
            _detailSourcesEmptyLine?.IsVisible = false;

            var dutyIx = 0;
            var chestIx = 0;
            foreach (var g in hierarchy) {
                var nonEmptyChests = g.ChestRows.Where(x => x.ItemIds.Count > 0).ToList();
                if (nonEmptyChests.Count == 0)
                    continue;

                while (_sourceDutyHeaderPool.Count <= dutyIx)
                    CreatePooledSourceDutyHeader(listWidth);
                var dh = _sourceDutyHeaderPool[dutyIx++];
                dh.String = g.DutyName;
                dh.IsVisible = true;
                if (ContentFinderCondition.GetRowRef(g.ContentFinderConditionId) is { RowId: > 0 })
                    _sourceDutyFinderCfcByHeader[dh] = g.ContentFinderConditionId;
                else
                    _sourceDutyFinderCfcByHeader.Remove(dh);

                foreach (var chest in nonEmptyChests) {
                    while (_sourceChestRowPool.Count <= chestIx)
                        CreatePooledSourceChestRow(listWidth);
                    var row = _sourceChestRowPool[chestIx++];
                    BindSourceChestRow(row, listWidth, chest);
                    row.Row.IsVisible = true;
                }
            }
        }

        RecalculateDetailPanels();
    }

    private void EnsureDetailPanelShell(float listWidth) {
        if (_detailPanelInitialized || _detailListNode is null)
            return;

        _detailEmptySection = new TreeComboSectionNode("Set Details", "No set selected", listWidth);
        _detailEmptyHint = new TextNode { Height = 20f, FontType = FontType.Axis, FontSize = 12, String = "Select a set from the list to view pieces.", TextColor = ImGuiColors.DalamudWhite };
        _detailEmptyHint.RemoveTextFlags(TextFlags.Emboss);
        _detailEmptySection.AddNode(_detailEmptyHint);

        _detailPiecesSection = new TreeComboSectionNode("Set Details", "", listWidth);

        _detailCostsSection = new TreeComboSectionNode("Costs", "", listWidth) { // TODO check for addon text
            IsVisible = false
        };

        _detailSourcesSection = new TreeComboSectionNode("Sources", listWidth) {
            IsVisible = false
        };
        _detailSourcesEmptyLine = new TextNode {
            Height = 20f,
            FontType = FontType.Axis,
            FontSize = 12,
            LineSpacing = 12,
            TextColor = ItemStatusGrey,
            Size = new Vector2(Math.Max(20f, listWidth - 8f), 20f),
            AlignmentType = AlignmentType.Left,
        };
        _detailSourcesEmptyLine.RemoveTextFlags(TextFlags.Emboss);
        _detailSourcesSection.AddNode(_detailSourcesEmptyLine);
        _detailSourcesEmptyLine.IsVisible = false;

        _detailListNode.AddNode(_detailEmptySection);
        _detailListNode.AddNode(_detailPiecesSection);
        _detailListNode.AddNode(_detailCostsSection);
        _detailListNode.AddNode(_detailSourcesSection);

        _detailPanelInitialized = true;
    }

    private void SyncDetailSectionWidths(float listWidth) {
        if (_detailEmptySection is { } e) {
            e.Width = listWidth;
            foreach (var h in e.HeaderNodes)
                h.Width = listWidth;
        }

        if (_detailPiecesSection is { } p) {
            p.Width = listWidth;
            foreach (var h in p.HeaderNodes)
                h.Width = listWidth;
        }

        if (_detailSourcesSection is { } s) {
            s.Width = listWidth;
            foreach (var h in s.HeaderNodes)
                h.Width = listWidth;
            _detailSourcesEmptyLine?.Size = new Vector2(Math.Max(20f, listWidth - 8f), 20f);
            foreach (var r in _sourceChestRowPool) {
                r.Row.Width = listWidth;
                r.Label.Size = new Vector2(SourceChestLabelWidth, SourceChestRowHeight - 4f);
            }
        }

        if (_detailCostsSection is { } c) {
            c.Width = listWidth;
            foreach (var h in c.HeaderNodes)
                h.Width = listWidth;
        }
    }

    private void ResetSourcesUiRows() {
        _sourceDutyFinderCfcByHeader.Clear();
        foreach (var h in _sourceDutyHeaderPool)
            h.IsVisible = false;
        foreach (var r in _sourceChestRowPool) {
            r.Row.IsVisible = false;
            foreach (var icon in r.Icons)
                icon.IsVisible = false;
        }
    }

    private void ResetCostsUiRows() {
        foreach (var h in _detailCostRowPool)
            h.Row.IsVisible = false;
        _detailCostCurrencyByRow.Clear();
    }

    private void RecalculateDetailPanels() {
        _detailEmptySection?.RecalculateLayout();
        _detailPiecesSection?.RecalculateLayout();
        _detailSourcesSection?.RecalculateLayout();
        _detailCostsSection?.RecalculateLayout();
        _detailListNode?.RecalculateLayout();
    }

    private void CreatePooledSourceDutyHeader(float listWidth) {
        if (_detailSourcesSection is null)
            return;
        var h = new TreeListHeaderNode {
            Width = listWidth,
            Height = 24f,
            String = string.Empty,
        };
        h.LabelNode.TextColor = ColorHelper.GetColor(7);
        h.LabelNode.Position = new Vector2(22f, 0f);
        h.LabelNode.RemoveTextFlags(TextFlags.Emboss);
        h.AddEvent(AtkEventType.MouseClick, (_, _, _, _, e) => OnSourceDutyHeaderClick(h, e));
        _detailSourcesSection.AddNode(h);
        _sourceDutyHeaderPool.Add(h);
    }

    private void OnSourceDutyHeaderClick(TreeListHeaderNode dutyHeader, AtkEventData* atkEventData) {
        if (atkEventData is null || _isFinalizing)
            return;
        if (!_sourceDutyFinderCfcByHeader.TryGetValue(dutyHeader, out var cfcId))
            return;
        if (!atkEventData->IsRightClick)
            return;
        OpenDutySourceContextMenu(cfcId);
    }

    private void CreatePooledSourceChestRow(float listWidth) {
        if (_detailSourcesSection is null)
            return;
        var row = new SimpleComponentNode {
            Height = SourceChestRowHeight,
            Width = listWidth,
        };
        var label = new TextNode {
            Position = new Vector2(4f, 2f),
            Size = new Vector2(SourceChestLabelWidth, SourceChestRowHeight - 4f),
            FontType = FontType.Axis,
            FontSize = 12,
            LineSpacing = 12,
            AlignmentType = AlignmentType.Left,
            TextColor = ImGuiColors.DalamudWhite,
        };
        label.RemoveTextFlags(TextFlags.Emboss);
        label.AttachNode(row);
        _detailSourcesSection.AddNode(row);
        _sourceChestRowPool.Add(new PooledSourceChestRow { Row = row, Label = label });
    }

    private static string FormatChestSourceLabel(ChestSourceRow chest)
        => chest.TerritoryTypeId == uint.MaxValue ? "FATE" : (chest.ChestNo != 0 ? $"Chest {chest.ChestNo}" : "Chest");

    private void BindSourceChestRow(PooledSourceChestRow h, float listWidth, ChestSourceRow chest) {
        h.Row.Width = listWidth;
        h.Label.String = FormatChestSourceLabel(chest);
        var n = chest.ItemIds.Count;
        var originX = SourceChestLabelWidth + 10f;
        var slot = Math.Max(0f, listWidth - originX - 6f);
        var iconSize = n == 0
            ? SourceLootIconSize
            : Math.Min(SourceLootIconSize, (slot - Math.Max(0, n - 1) * SourceLootIconGap) / n);
        iconSize = Math.Max(16f, iconSize);
        var iconY = (SourceChestRowHeight - iconSize) * 0.5f;

        for (var i = 0; i < n; i++) {
            while (h.Icons.Count <= i) {
                var icon = new FramedItemIconNode(SourceLootIconSize);
                icon.AttachNode(h.Row);
                h.Icons.Add(icon);
            }
            var img = h.Icons[i];
            img.SetItemId(chest.ItemIds[i]);
            img.Size = new Vector2(iconSize, iconSize);
            img.Position = new Vector2(originX + i * (iconSize + SourceLootIconGap), iconY);
            img.IsVisible = true;
        }
        for (var j = n; j < h.Icons.Count; j++)
            h.Icons[j].IsVisible = false;
    }

    private void CreatePooledPieceRow(float listWidth) {
        if (_detailPiecesSection is null)
            return;

        var itemNode = new ListButtonNode {
            Height = ListRowHeight,
            Width = listWidth,
            String = string.Empty,
            Selected = false,
        };
        itemNode.LabelNode.Position = new Vector2(DetailLabelLeft, 1f);

        var iconNode = new FramedItemIconNode(ListIconSize) {
            Position = new Vector2(ListIconPadX, ListIconPadY),
            Size = new Vector2(ListIconSize, ListIconSize),
        };
        iconNode.AttachNode(itemNode);

        var statusNode = new TextNode {
            FontType = FontType.Axis,
            FontSize = 12,
            LineSpacing = 12,
            AlignmentType = AlignmentType.Right,
            TextColor = ItemStatusGrey,
        };
        statusNode.AttachNode(itemNode);
        var storageBadge = new GlamourIconNode(GlamourIconNode.IconPart.Dresser);
        storageBadge.AttachNode(itemNode);

        itemNode.AddEvent(AtkEventType.MouseClick, (_, _, _, _, e) => OnDetailPieceRowClick(itemNode, e));

        var h = new PooledDetailPieceRow {
            Button = itemNode,
            Icon = iconNode,
            Status = statusNode,
            StorageBadge = storageBadge,
            LastStorageIconPart = GlamourIconNode.IconPart.Dresser,
        };
        _detailPieceRowPool.Add(h);
        _detailPiecesSection.AddNode(itemNode);
    }

    private void BindDetailPieceRow(PooledDetailPieceRow h, float listWidth, uint itemId, HashSet<uint> ownedItems, HashSet<uint> inventoryItems) {
        var itemNode = h.Button;
        itemNode.Width = listWidth;
        itemNode.Selected = _sourceFilterPieceItemId == itemId;
        var itemRow = Item.GetRow(itemId);
        itemNode.LabelNode.String = itemRow.Name.ToString();
        itemNode.LabelNode.TextColor = ColorHelper.GetColor(itemRow.AtkUiRarityColorId);
        itemNode.LabelNode.Size = new Vector2(
            Math.Max(20f, itemNode.Width - DetailLabelLeft - DetailStatusWidth - 4f),
            itemNode.Height - 1f);
        itemNode.ItemTooltip = itemId;

        h.Icon.SetItemId(itemId);
        h.Icon.Position = new Vector2(ListIconPadX, ListIconPadY);
        h.Icon.Size = new Vector2(ListIconSize, ListIconSize);
        h.Status.Position = new Vector2(itemNode.Width - DetailStatusWidth, 1f);
        h.Status.Size = new Vector2(DetailStatusWidth - 4f, itemNode.Height - 2f);
        h.Status.String = FormatItemStorageStatus(itemId, ownedItems, inventoryItems);
        var storageState = Svc.Ownership.GetItemStorageState(itemId, _selectedSet);
        if (StorageIconPartFor(storageState) is { } part) {
            h.StorageBadge.IsVisible = true;
            if (h.LastStorageIconPart != part) {
                h.StorageBadge.SetPart(part);
                h.LastStorageIconPart = part;
            }

            h.StorageBadge.Position = new Vector2(Math.Max(0f, itemNode.Width - h.StorageBadge.Size.X - 4f), 2f);
        }
        else
            h.StorageBadge.IsVisible = false;
        _detailPieceItemByRow[itemNode] = itemId;
    }

    private void OnDetailPieceRowClick(ListButtonNode itemNode, AtkEventData* atkEventData) {
        if (atkEventData is null || _isFinalizing)
            return;
        if (!_detailPieceItemByRow.TryGetValue(itemNode, out var itemId))
            return;

        ref var eventData = ref *atkEventData;
        if (eventData.IsLeftClick) {
            _sourceFilterPieceItemId = _sourceFilterPieceItemId == itemId ? null : itemId;
            _pendingPaintDetailsOnly = true;
            return;
        }

        if (eventData.IsRightClick)
            OpenItemContextMenu(itemId);
    }

    private void CreatePooledCostRow(float listWidth) {
        if (_detailCostsSection is null)
            return;

        var row = new GatheringNoteItemNode(SetListRowHeight, 29f, SetTitleWhite) {
            Width = listWidth,
            Selected = false,
            String = string.Empty,
        };
        row.CheckBadge.IsVisible = false;
        row.AddEvent(AtkEventType.MouseClick, (_, _, _, _, e) => OnDetailCostRowClick(row, e));

        var h = new PooledDetailCostRow { Row = row };
        _detailCostRowPool.Add(h);
        _detailCostsSection.AddNode(row);
    }

    private void BindCostRow(PooledDetailCostRow h, float listWidth, uint costItemId, uint amountRequired) {
        var row = h.Row;
        var rowWidth = Math.Max(listWidth, _detailListNode is { } d && d.Size.X > 8f ? d.Size.X : listWidth);
        row.Width = rowWidth;
        row.Height = SetListRowHeight;
        row.Selected = false;
        row.CheckBadge.IsVisible = false;

        var currencyItem = Item.GetRow(costItemId);
        row.IconNode.IconId = currencyItem.Icon;
        row.ItemTooltip = costItemId;
        row.TitleNode.String = currencyItem.Name.ToString();
        row.TitleNode.TextColor = SetTitleWhite;

        var owned = GetOwnedCurrencyCount(costItemId);
        row.SubtitleNode.String = $"Obt. {owned}/{amountRequired}";

        _detailCostCurrencyByRow[row] = costItemId;
    }

    private void OnDetailCostRowClick(ListButtonNode row, AtkEventData* atkEventData) {
        if (atkEventData is null || _isFinalizing)
            return;
        if (!_detailCostCurrencyByRow.TryGetValue(row, out var costItemId))
            return;

        ref var eventData = ref *atkEventData;
        if (!eventData.IsRightClick)
            return;
        OpenItemContextMenu(costItemId);
    }

    private bool TryGetCostTotals(GlamourSet set, uint? pieceFilterPieceItemId, out Dictionary<uint, uint> totals) {
        totals = [];
        IEnumerable<uint> pieceIds = pieceFilterPieceItemId is { } only ? [only] : set.Items;
        foreach (var itemId in pieceIds) {
            foreach (var (cid, amt) in Svc.Catalog.GetPrimaryItemCosts(itemId, Svc.Catalog.CategoryNameForPrimaryCostLookup(set))) {
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

    private static string SetSublineText(GlamourSet set, HashSet<GlamourSet> ownedSets, HashSet<uint> ownedItems) {
        var n = set.Items.Count;
        var c = set.Items.Count(ownedItems.Contains);
        if (ownedSets.Contains(set))
            return $"Obt. {n}/{n}";
        if (n == 0)
            return "Obt. 0/0";
        if (c == n)
            return "Completable";
        return $"Obt. {c}/{n}";
    }

    private void OpenSetContextMenu(GlamourSet set) {
        _contextMenu.Clear();
        _contextMenu.AddItem($"{Addon.GetRow(2426).Text} ({Addon.GetRow(1043).Text})", () => {
            MemoryHelper.WriteField(AgentTryon.Instance(), 0x366, true);
            set.Items.ForEach(i => AgentTryon.TryOn(0, i));
        });
        _contextMenu.Open();
    }

    private void OpenItemContextMenu(uint itemId) {
        var item = Item.GetRow(itemId);
        var itemName = item.Name.ToString();
        _contextMenu.Clear();

        _contextMenu.AddItem(Addon.GetRow(4379).Text, () => Svc.CommandManager.ProcessCommand($"/isearch {EscapeText(itemName)}"));
        _contextMenu.AddItem("Link Item In Chat", () => {
            try { Svc.Chat.Print(SeString.CreateItemLink(itemId, false)); } catch { }
        });
        _contextMenu.AddItem(Addon.GetRow(159).Text, () => ImGui.SetClipboardText(itemName));
        _contextMenu.AddItem(Addon.GetRow(2426).Text, () => AgentTryon.TryOn(0, itemId));

        if (Recipe.FirstOrNull(r => r.RowId > 0 && r.ItemResult.RowId == itemId) is { RowId: var id }) {
            _contextMenu.AddItem("Open Recipe", () => AgentRecipeNote.Instance()->OpenRecipeByRecipeId(id));
        }

        if (!item.IsUntradable) {
            if (Svc.PluginInterface.IsPluginLoaded("MarketBoardPlugin")) {
                _contextMenu.AddItem("Open In MarketBoardPlugin", () => Svc.CommandManager.ProcessCommand($"/pmb {itemId}"));
            }
            if (Svc.PluginInterface.IsPluginLoaded("vmarket")) {
                _contextMenu.AddItem("Open In vmarket", () => Svc.CommandManager.ProcessCommand($"/vmarket {itemId}"));
            }
        }

        _contextMenu.Open();
    }

    private void OpenDutySourceContextMenu(uint contentFinderConditionId) {
        if (contentFinderConditionId == 0)
            return;

        _contextMenu.Clear();
        _contextMenu.AddItem(Addon.GetRow(15890).Text, () => AgentContentsFinder.Instance()->OpenRegularDuty(contentFinderConditionId));
        _contextMenu.AddItem($"{Addon.GetRow(9663).Text} ({Addon.GetRow(1145).Text})", () => {
            if (ContentFinderCondition.GetRowRef(contentFinderConditionId) is { IsValid: true, Value: var cfc })
                cfc.QueueDuty(levelSync: true);
        });
        _contextMenu.AddItem($"{Addon.GetRow(9663).Text} ({Addon.GetRow(10008).Text})", () => {
            if (ContentFinderCondition.GetRowRef(contentFinderConditionId) is { IsValid: true, Value: var cfc })
                cfc.QueueDuty(levelSync: false);
        });
        if (Svc.PluginInterface.IsPluginLoaded("AutoDuty")) {
            _contextMenu.AddItem("AutoDuty", () => {
                // TODO
            });
        }
        _contextMenu.Open();
    }

    private static string EscapeText(string text) => $"\"{text.Replace("\"", "\\\"")}\"";

    private static GlamourIconNode.IconPart? StorageIconPartFor(ItemStorageState storageState)
        => storageState switch {
            ItemStorageState.Armoire => GlamourIconNode.IconPart.Armoire,
            ItemStorageState.DresserLoose => GlamourIconNode.IconPart.DresserFaded,
            ItemStorageState.DresserSet => GlamourIconNode.IconPart.Dresser,
            _ => null,
        };

    private string FormatItemStorageStatus(uint itemId, HashSet<uint> ownedItems, HashSet<uint> inventoryItems) {
        if (!ownedItems.Contains(itemId) && !Svc.Catalog.ArmoireItemIds.Contains(itemId))
            return "\u2014";
        var storageState = Svc.Ownership.GetItemStorageState(itemId, _selectedSet);
        var d = storageState is ItemStorageState.DresserSet or ItemStorageState.DresserLoose;
        var a = storageState is ItemStorageState.Armoire;
        var i = inventoryItems.Contains(itemId);
        if (!d && !a && !i)
            return "\u2022";
        return (d ? "D" : "") + (d && (a || i) ? "\u00B7" : "") + (a ? "A" : "") + (a && i ? "\u00B7" : "") + (i ? "I" : "");
    }
}
