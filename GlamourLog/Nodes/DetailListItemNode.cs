using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Extensions;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using KamiToolKit.BaseTypes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes;

internal enum SourceIconPresentation {
    Normal,
    Large,
}

internal enum DetailRowKind {
    SectionHeader,
    JournalHeader,
    EmptyHint,
    Piece,
    Cost,
    SourceDuty,
    SourceChest,
    SourceArrowFlow,
    SharedModelSet,
}

internal sealed class DetailListRowData {
    public required DetailRowKind Kind { get; init; }
    public string PrimaryText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public bool IsSelected { get; init; }
    public GlamourIconNode.IconPart? StorageIconPart { get; init; }
    public bool ShowInventoryBadge { get; init; }
    public bool ShowArmoireWarning { get; init; }
    public uint ContentFinderConditionId { get; init; }
    public IReadOnlyList<uint>? SourceItemIds { get; init; }
    public SourceIconPresentation SourcePresentation { get; init; } // icon size
    public uint CraftRecipeRowId { get; init; } // creates an open-recipe click when set
    public SourceNavigateTarget? NavigateTarget { get; init; }
    public string CostVendorTextTooltip { get; init; } = string.Empty;
    public string CostMapFlagLabel { get; init; } = string.Empty;
    public bool SourceIconsOnly { get; init; }
    public int SourceIconOverflow { get; init; } // # icons not shown when SourceItemIds exceeds space
    public IReadOnlyList<uint>? SourceFlowLeftIds { get; init; } // left strip ids for SourceArrowFlow, right is SourceItemIds
    public bool IsTopLevelSection { get; init; }
    public SetListRowData? SharedModelRow { get; init; }
    public uint SharedModelItemId { get; init; } // shared model row represents this id for piece filter scope
    public float SourceChestLabelColumnWidth { get; init; } // duty-wide label column for aligned icon strips; 0 = per-row
}

internal sealed unsafe class DetailListItemNode : ListItemNode<DetailListRowData>, IListItemNode {
    public static float ItemHeight => 30f;
    private const float PieceIconSize = 22f;
    private const float PieceTextBoxHeight = 19f; // ellipsis needs extra height vs line size
    private const float DetailIconX = 2f;
    private const float DetailIconTextX = 30f;
    private const float DutyChestLabelX = 6f;
    private const float DutyChestLabelPadding = 4f;
    private const float DutyChestLabelIconGap = 16f;
    private const float DutyChestLabelMinWidth = 36f;
    private static float PieceIconY => (ItemHeight - PieceIconSize) * 0.5f;

    public Action<uint>? OnPieceLeftClick { get; set; }
    public Action<uint>? OnItemRightClick { get; set; }
    public Action<uint, SourceNavigateTarget?>? OnSourceHeaderRightClick { get; set; }
    public Action<SourceNavigateTarget, string>? OnSourceMapFlagLeftClick { get; set; }
    public Action<uint>? OnCraftRecipeJournalLeftClick { get; set; }
    public Action<string, bool>? OnDetailSectionToggle { get; set; } // fired when SectionHeader is toggled
    public Func<string, bool>? IsDetailSectionCollapsed { get; set; } // restore collapsed state for headers are rebuild if true
    public Action<GlamourSet>? OnSharedModelSetLeftClick { get; set; }
    public Action<uint, GlamourSet>? OnSharedModelItemLeftClick { get; set; }

    private readonly RowFramedIcon _pieceIcon;
    private readonly CollisionNode _inputCollision;
    private readonly TextNode _primary;
    private readonly TextNode _secondary;
    private readonly TreeComboSectionNode _sectionChrome;
    private readonly TreeListHeaderNode _journalChrome;
    private readonly GlamourIconNode _storageBadge;
    private readonly InventoryBadgeNode _inventoryBadge;
    private readonly ArmoireWarningBadgeNode _armoireWarningBadge;
    private readonly List<RowFramedIcon> _sourceIcons = [];
    private readonly TextNode _sourceOverflow;
    private readonly SourceFlowNode _arrowFlow;
    private readonly CheckMarkBadgeNode _checkBadge;
    private GlamourIconNode.IconPart _lastStoragePart = GlamourIconNode.IconPart.Dresser;

    public DetailListItemNode() {
        // custom collision + HandleClick; SelectableNode row chrome would duplicate/conflict with hit targets below
        EnableSelection = false;
        EnableHighlight = false;

        // flat ResNode icon siblings (same coordinate space as text/header nodes)
        _pieceIcon = new RowFramedIcon();
        _pieceIcon.AttachTo(this);

        _sectionChrome = new TreeComboSectionNode(string.Empty, 200f) {
            IsVisible = false,
            Height = 24f,
        };
        // tree combo collision doesn't register on this row as a sibling; clicks go through _inputCollision + HandleClick
        _sectionChrome.CollisionNode.NodeFlags = 0;
        _sectionChrome.AttachNode(this);

        _journalChrome = new TreeListHeaderNode {
            Width = 200f,
            Height = 24f,
            IsVisible = false,
        };
        _journalChrome.LabelNode.TextColor = new Vector4(0f, 0f, 0f, 1f);
        _journalChrome.LabelNode.Position = new Vector2(22f, 0f);
        _journalChrome.LabelNode.RemoveTextFlags(TextFlags.Emboss);
        _journalChrome.AttachNode(this);

        _primary = new TextNode {
            Position = new Vector2(DetailIconTextX, 1f),
            Size = new Vector2(220f, 14f),
            FontType = FontType.Axis,
            FontSize = 12,
            LineSpacing = 12,
            AlignmentType = AlignmentType.Left,
            TextColor = ImGuiColors.DalamudWhite,
        };
        _primary.RemoveTextFlags(TextFlags.Emboss);
        _primary.AddTextFlags(TextFlags.Ellipsis);
        _primary.AttachNode(this);

        _secondary = new TextNode {
            Position = new Vector2(DetailIconTextX, 14f),
            Size = new Vector2(220f, 14f),
            FontType = FontType.Axis,
            FontSize = 12,
            LineSpacing = 12,
            AlignmentType = AlignmentType.Left,
            TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f),
            IsVisible = false,
        };
        _secondary.RemoveTextFlags(TextFlags.Emboss);
        _secondary.AttachNode(this);

        _storageBadge = new GlamourIconNode(GlamourIconNode.IconPart.Dresser) {
            IsVisible = false,
        };
        _storageBadge.AttachNode(this);

        _inventoryBadge = new InventoryBadgeNode {
            IsVisible = false,
        };
        _inventoryBadge.AttachNode(this);

        _armoireWarningBadge = new ArmoireWarningBadgeNode {
            IsVisible = false,
        };
        _armoireWarningBadge.AttachNode(this);

        _inputCollision = new CollisionNode {
            CollisionType = CollisionType.Hit,
            Uses = 0,
            ShowClickableCursor = true
        };
        _inputCollision.AddDrawFlags(DrawFlags.ClickableCursor);
        // ktk: native click on this CollisionNode (SelectableNode row hit is unused while EnableSelection is off)
        _inputCollision.AddEvent(AtkEventType.MouseClick, (_, _, _, _, eventData) => HandleClick(eventData));
        // after source icons so each icon keeps hits + item tooltip
        _inputCollision.AttachNode(this);

        for (var i = 0; i < 12; i++) {
            var icon = new RowFramedIcon();
            icon.AttachTo(this);
            _sourceIcons.Add(icon);
        }

        _sourceOverflow = new TextNode {
            IsVisible = false,
            FontType = FontType.Axis,
            FontSize = 11,
            LineSpacing = 11,
            AlignmentType = AlignmentType.Left,
            TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f),
        };
        _sourceOverflow.RemoveTextFlags(TextFlags.Emboss);
        _sourceOverflow.AttachNode(this);

        _arrowFlow = new SourceFlowNode { IsVisible = false };
        _arrowFlow.AttachNode(this);

        _checkBadge = new CheckMarkBadgeNode { IsVisible = false };
        _checkBadge.AttachNode(this);
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();

        _primary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);
        _secondary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);
        _sectionChrome.Width = Width;
        _sectionChrome.Size = new Vector2(Width, _sectionChrome.Height);
        _sectionChrome.Position = new Vector2(0f, 3f);
        foreach (var header in _sectionChrome.HeaderNodes)
            header.Width = Width;
        _journalChrome.Width = Width;
        _journalChrome.Position = new Vector2(0f, 3f);
        var badgeY = ItemData?.Kind == DetailRowKind.Piece ? (ItemHeight - _storageBadge.Size.Y) * 0.5f : 3f;
        _storageBadge.Position = new Vector2(Math.Max(0f, Width - _storageBadge.Size.X - 12f), badgeY);
        _inventoryBadge.Position = new Vector2(Math.Max(0f, Width - _inventoryBadge.Size.X - 12f), badgeY);
        _armoireWarningBadge.Position = _storageBadge.Position + new Vector2(
            _storageBadge.Size.X - _armoireWarningBadge.Size.X,
            _storageBadge.Size.Y - _armoireWarningBadge.Size.Y);

        if (_pieceIcon.IsVisible)
            LayoutIconRow();

        _checkBadge.Position = new Vector2(
            DetailIconX + PieceIconSize - _checkBadge.Size.X - 2f,
            PieceIconY + PieceIconSize - _checkBadge.Size.Y);

        _inputCollision.Position = Vector2.Zero;
        _inputCollision.Size = Size;

        // row width from parent updated; pooled ItemData may not re-run SetNodeData same frame
        if (ItemData is not null)
            ApplyDynamicWidth(ItemData);
    }

    private void LayoutIconRow() {
        _pieceIcon.Layout(new Vector2(DetailIconX, PieceIconY), PieceIconSize);
    }

    private void ApplyDynamicWidth(DetailListRowData data) {
        switch (data.Kind) {
            case DetailRowKind.EmptyHint:
                // same quirk as Piece: 14px-tall text boxes ellipsize with horizontal room left
                _primary.Size = new Vector2(Math.Max(20f, Width - 8f), 19f);
                break;
            case DetailRowKind.Piece:
                var pieceTextRightReserve = 8f;
                if (_storageBadge.IsVisible)
                    pieceTextRightReserve = _storageBadge.Size.X + 16f;
                else if (_inventoryBadge.IsVisible)
                    pieceTextRightReserve = _inventoryBadge.Size.X + 16f;
                _primary.Size = new Vector2(Math.Max(20f, Width - 30f - pieceTextRightReserve), PieceTextBoxHeight);
                _primary.Position = new Vector2(DetailIconTextX, PieceIconY + (PieceIconSize - PieceTextBoxHeight) * 0.5f);
                break;
            case DetailRowKind.Cost:
            case DetailRowKind.SharedModelSet:
                _primary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);
                _secondary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);
                break;
            case DetailRowKind.SourceChest:
                float iconOriginX;
                if (data.SourceIconsOnly)
                    iconOriginX = 4f;
                else if (data.PrimaryText.Length > 0) {
                    var hasChestType = data.SecondaryText.Length > 0;
                    var labelWidth = MeasureDutyChestLabelWidth(
                        data.PrimaryText,
                        hasChestType ? data.SecondaryText : string.Empty);
                    var labelColumnWidth = data.SourceChestLabelColumnWidth > 0f
                        ? data.SourceChestLabelColumnWidth
                        : labelWidth;
                    _primary.Size = new Vector2(labelWidth, hasChestType ? 12f : 14f);
                    if (hasChestType)
                        _secondary.Size = new Vector2(labelWidth, 12f);
                    iconOriginX = DutyChestLabelX + labelColumnWidth + DutyChestLabelIconGap;
                }
                else {
                    var hasSourceSecondary = data.SecondaryText.Length > 0;
                    _primary.Size = new Vector2(Math.Max(20f, Width - 180f), hasSourceSecondary ? 12f : 14f);
                    if (hasSourceSecondary)
                        _secondary.Size = new Vector2(Math.Max(20f, Width - 180f), 14f);
                    iconOriginX = 4f;
                }

                if (data.SourceItemIds is { Count: > 0 } sourceItems) {
                    var large = data.SourcePresentation == SourceIconPresentation.Large;
                    var iconSize = large ? 26f : 24f;
                    var iconGap = large ? 5f : 4f;
                    var iconY = (ItemHeight - iconSize) * 0.5f;
                    var maxFit = (int)Math.Max(1, Math.Floor((Width - iconOriginX - 40f) / (iconSize + iconGap)));
                    var show = Math.Min(Math.Min(sourceItems.Count, _sourceIcons.Count), maxFit);
                    for (var i = 0; i < _sourceIcons.Count; i++) {
                        var icon = _sourceIcons[i];
                        if (i < show) {
                            icon.SetItemId(sourceItems[i]);
                            icon.Layout(new Vector2(iconOriginX + i * (iconSize + iconGap), iconY), iconSize);
                            icon.IsVisible = true;
                        }
                        else {
                            icon.IsVisible = false;
                        }
                    }

                    if (data.SourceIconOverflow > 0) {
                        _sourceOverflow.String = $"+{data.SourceIconOverflow}";
                        _sourceOverflow.Position = new Vector2(iconOriginX + show * (iconSize + iconGap) + 2f, iconY + 3f);
                        _sourceOverflow.IsVisible = true;
                    }
                    else {
                        _sourceOverflow.IsVisible = false;
                    }
                }
                else {
                    foreach (var icon in _sourceIcons)
                        icon.IsVisible = false;
                    _sourceOverflow.IsVisible = false;
                }

                break;
            case DetailRowKind.SourceArrowFlow:
                _arrowFlow.Size = new Vector2(Math.Max(0f, Width - 8f), ItemHeight);
                _arrowFlow.SetFlow(data.SourceFlowLeftIds ?? [], data.SourceItemIds ?? [], data.SourceIconOverflow);
                break;
        }
    }

    protected override void SetNodeData(DetailListRowData itemData) {
        _pieceIcon.IsVisible = false;
        _sectionChrome.IsVisible = false;
        _journalChrome.IsVisible = false;
        _secondary.IsVisible = false;
        _storageBadge.IsVisible = false;
        _inventoryBadge.IsVisible = false;
        _armoireWarningBadge.IsVisible = false;
        foreach (var icon in _sourceIcons)
            icon.IsVisible = false;
        _sourceOverflow.IsVisible = false;
        _arrowFlow.IsVisible = false;
        _arrowFlow.Hide();
        _checkBadge.IsVisible = false;

        _inputCollision.ItemTooltip = 0;
        _inputCollision.TextTooltip = string.Empty;
        _inputCollision.IsVisible = true;

        _primary.IsVisible = true;
        _primary.String = itemData.PrimaryText;
        _secondary.String = itemData.SecondaryText;
        _primary.FontSize = 12;
        _primary.LineSpacing = 12;
        _primary.AlignmentType = AlignmentType.Left;
        _primary.TextColor = ImGuiColors.DalamudWhite;
        _primary.Position = new Vector2(DetailIconTextX, 1f);
        _secondary.Position = new Vector2(DetailIconTextX, 14f);
        _inputCollision.CollisionType = CollisionType.Hit;
        _inputCollision.ShowClickableCursor = false;

        // pooled rows keep SourceChest's narrow primary width; reset before kind-specific layout
        _primary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);
        _secondary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);

        // piece branch strips this; atk ellipsis on names was wrong more often than clip
        _primary.AddTextFlags(TextFlags.Ellipsis);

        switch (itemData.Kind) {
            case DetailRowKind.SectionHeader:
                _primary.String = string.Empty;
                _sectionChrome.IsVisible = true;
                _sectionChrome.String = itemData.PrimaryText;
                if (IsDetailSectionCollapsed is { } collapsedFn) {
                    var wantCollapsed = collapsedFn(itemData.PrimaryText);
                    if (_sectionChrome.IsCollapsed != wantCollapsed)
                        _sectionChrome.IsCollapsed = wantCollapsed;
                }
                _inputCollision.IsVisible = true;
                _inputCollision.ShowClickableCursor = true;
                break;
            case DetailRowKind.JournalHeader:
                _primary.String = string.Empty;
                _journalChrome.IsVisible = true;
                _journalChrome.String = itemData.PrimaryText;
                var journalClickable = itemData.CraftRecipeRowId != 0
                    || itemData.NavigateTarget is { TerritoryTypeId: var jt } && jt != 0;
                _inputCollision.ShowClickableCursor = journalClickable;
                break;
            case DetailRowKind.EmptyHint:
                _primary.Position = new Vector2(4f, 5f);
                _primary.TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f);
                _primary.RemoveTextFlags(TextFlags.Ellipsis);
                break;
            case DetailRowKind.Piece:
                var itemRow = Item.GetRow(itemData.ItemId);
                _pieceIcon.SetItemId(itemData.ItemId);
                _pieceIcon.IsVisible = true;
                // Left = middle-left in atk; Center also centers horizontally in the text box
                _primary.AlignmentType = AlignmentType.Left;
                _primary.FontSize = 14;
                _primary.LineSpacing = 14;
                _primary.TextColor = itemData.IsSelected ? new Vector4(216f / 255f, 187f / 255f, 125f / 255f, 1f) : ColorHelper.GetColor(itemRow.AtkUiRarityColorId);
                if (itemData.StorageIconPart is { } storagePart) {
                    if (_lastStoragePart != storagePart) {
                        _storageBadge.SetPart(storagePart);
                        _lastStoragePart = storagePart;
                    }
                    _storageBadge.IsVisible = true;
                    _armoireWarningBadge.IsVisible = itemData.ShowArmoireWarning;
                }
                else if (itemData.ShowInventoryBadge) {
                    _inventoryBadge.IsVisible = true;
                }
                _inputCollision.ShowClickableCursor = true;
                _inputCollision.ItemTooltip = itemData.ItemId;
                _primary.RemoveTextFlags(TextFlags.Ellipsis);
                break;
            case DetailRowKind.Cost:
                _pieceIcon.SetItemId(itemData.ItemId);
                _pieceIcon.IsVisible = true;
                ApplyIconTwoLineTextLayout(itemData.PrimaryText, itemData.SecondaryText);
                _inputCollision.ShowClickableCursor = true;
                _inputCollision.ItemTooltip = itemData.ItemId;
                if (itemData.CostVendorTextTooltip.Length > 0)
                    _inputCollision.TextTooltip = itemData.CostVendorTextTooltip;
                break;
            case DetailRowKind.SourceDuty:
                _primary.String = string.Empty;
                _journalChrome.IsVisible = true;
                _journalChrome.String = itemData.PrimaryText;
                var dutyHeaderClick = itemData.ContentFinderConditionId != 0 || itemData.NavigateTarget is not null;
                _inputCollision.ShowClickableCursor = dutyHeaderClick;
                break;
            case DetailRowKind.SourceChest:
                var iconOnlyChest = itemData.SourceIconsOnly;
                if (iconOnlyChest) {
                    _primary.String = string.Empty;
                    _primary.IsVisible = false;
                    _secondary.IsVisible = false;
                }
                else if (itemData.PrimaryText.Length > 0) {
                    _primary.String = itemData.PrimaryText;
                    _primary.IsVisible = true;
                    _primary.FontSize = 12;
                    _primary.LineSpacing = 12;
                    _primary.TextColor = ImGuiColors.DalamudWhite;
                    _primary.RemoveTextFlags(TextFlags.Ellipsis);
                    _secondary.RemoveTextFlags(TextFlags.Ellipsis);
                    if (itemData.SecondaryText.Length == 0) {
                        _primary.Position = new Vector2(DutyChestLabelX, (ItemHeight - 12f) * 0.5f);
                        _secondary.IsVisible = false;
                    }
                    else {
                        _primary.Position = new Vector2(DutyChestLabelX, 2f);
                        _secondary.IsVisible = true;
                        _secondary.Position = new Vector2(DutyChestLabelX, 16f);
                        _secondary.FontSize = 12;
                        _secondary.LineSpacing = 12;
                        _secondary.TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f);
                    }
                }
                else {
                    var hasSourceSecondary = itemData.SecondaryText.Length > 0;
                    _primary.IsVisible = true;
                    _primary.Position = hasSourceSecondary ? new Vector2(4f, 2f) : new Vector2(4f, 10f);
                    if (hasSourceSecondary) {
                        _secondary.IsVisible = true;
                        _secondary.Position = new Vector2(4f, 16f);
                        _secondary.TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f);
                    }
                }

                if (!iconOnlyChest)
                    _primary.IsVisible = true;
                // full-row hitbox would steal clicks from per-icon tooltips / item hits
                _inputCollision.IsVisible = false;
                break;
            case DetailRowKind.SourceArrowFlow:
                _primary.IsVisible = false;
                _secondary.IsVisible = false;
                _arrowFlow.Position = new Vector2(4f, 0f);
                _arrowFlow.IsVisible = true;
                // same as SourceChest: row collision would eat icon hits
                _inputCollision.IsVisible = false;
                break;
            case DetailRowKind.SharedModelSet:
                if (itemData.SharedModelRow is not { } sharedRow)
                    break;
                var sharedIconId = sharedRow.IconItemId != 0
                    ? sharedRow.IconItemId
                    : sharedRow.Set.NonSetCabinetPiece ? sharedRow.Set.Items[0] : sharedRow.Set.ItemId;
                _pieceIcon.SetItemId(sharedIconId);
                _pieceIcon.IsVisible = true;
                ApplyIconTwoLineTextLayout(sharedRow.Title, sharedRow.Subtitle);
                _checkBadge.IsVisible = sharedRow.IsOwned;
                if (sharedRow.ShowStorage) {
                    if (_lastStoragePart != sharedRow.StorageIconPart) {
                        _storageBadge.SetPart(sharedRow.StorageIconPart);
                        _lastStoragePart = sharedRow.StorageIconPart;
                    }
                    _storageBadge.IsVisible = true;
                    _armoireWarningBadge.IsVisible = sharedRow.ShowArmoireWarning;
                }
                _inputCollision.IsVisible = true;
                _inputCollision.ShowClickableCursor = true;
                _inputCollision.ItemTooltip = sharedIconId;
                break;
        }

        ApplyDynamicWidth(itemData);
        NudgeTextLayoutIfNeeded(itemData.Kind);
    }

    private static readonly Vector4 TwoLineSubtitleColor = new(157f / 255f, 131f / 255f, 91f / 255f, 1f);

    internal static float MeasureDutyChestLabelColumnWidth(TextNode measure, string primaryText, string secondaryText) {
        measure.RemoveTextFlags(TextFlags.Emboss);
        var width = measure.GetTextDrawSize((ReadOnlySeString)primaryText).X;
        if (secondaryText.Length > 0)
            width = Math.Max(width, measure.GetTextDrawSize((ReadOnlySeString)secondaryText).X);
        return Math.Max(DutyChestLabelMinWidth, MathF.Ceiling(width) + DutyChestLabelPadding);
    }

    private float MeasureDutyChestLabelWidth(string primaryText, string secondaryText)
        => MeasureDutyChestLabelColumnWidth(_primary, primaryText, secondaryText);

    private void ApplyIconTwoLineTextLayout(string primaryText, string secondaryText) {
        LayoutIconRow();
        _primary.String = primaryText;
        _primary.Position = new Vector2(DetailIconTextX, 1f);
        _primary.FontSize = 12;
        _primary.LineSpacing = 12;
        _primary.AlignmentType = AlignmentType.Left;
        _primary.TextColor = ImGuiColors.DalamudWhite;
        _primary.RemoveTextFlags(TextFlags.Ellipsis);
        _secondary.String = secondaryText;
        _secondary.IsVisible = true;
        _secondary.Position = new Vector2(DetailIconTextX, 15f);
        _secondary.FontSize = 12;
        _secondary.LineSpacing = 12;
        _secondary.TextColor = TwoLineSubtitleColor;
    }

    // atk caches ellipsis metrics until string is toggled after a native draw pass
    private void NudgeTextLayoutIfNeeded(DetailRowKind kind) {
        var shouldNudge = kind switch {
            DetailRowKind.Cost or DetailRowKind.Piece or DetailRowKind.EmptyHint => true,
            DetailRowKind.SourceChest => ItemData is { SourceIconsOnly: false, PrimaryText.Length: > 0 },
            _ => false,
        };
        if (!shouldNudge)
            return;
        NudgeTextNode(_primary);
        if (_secondary.IsVisible)
            NudgeTextNode(_secondary);
    }

    private static void NudgeTextNode(TextNode node) {
        var text = node.String.ToString();
        if (text.Length == 0)
            return;
        node.String = string.Empty;
        node.String = text;
    }

    private void HandleClick(AtkEventData* eventData) {
        if (eventData is null || ItemData is null)
            return;

        if (eventData->IsLeftClick) {
            if (ItemData.Kind is DetailRowKind.SectionHeader && ItemData.PrimaryText.Length > 0 && OnDetailSectionToggle is not null && IsDetailSectionCollapsed is not null) {
                var title = ItemData.PrimaryText;
                // Second argument: true = expand (remove from collapsed set); matches IsDetailSectionCollapsed true when currently collapsed.
                var collapsedBefore = IsDetailSectionCollapsed(title);
                OnDetailSectionToggle(title, collapsedBefore);
                _sectionChrome.IsCollapsed = IsDetailSectionCollapsed(title);
                return;
            }

            if (ItemData.Kind is DetailRowKind.Piece && ItemData.ItemId != 0) {
                OnPieceLeftClick?.Invoke(ItemData.ItemId);
                return;
            }

            if (ItemData.Kind is DetailRowKind.Cost && ItemData.NavigateTarget is { TerritoryTypeId: var costTid } && costTid != 0) {
                var mapLabel = ItemData.CostMapFlagLabel.Length > 0
                    ? ItemData.CostMapFlagLabel
                    : Item.GetRow(ItemData.ItemId).Name.ToString();
                OnSourceMapFlagLeftClick?.Invoke(ItemData.NavigateTarget.Value, mapLabel);
                return;
            }

            if (ItemData.Kind is DetailRowKind.JournalHeader) {
                if (ItemData.CraftRecipeRowId != 0) {
                    OnCraftRecipeJournalLeftClick?.Invoke(ItemData.CraftRecipeRowId);
                    return;
                }

                if (ItemData.NavigateTarget is { } nav && nav.TerritoryTypeId != 0) {
                    OnSourceMapFlagLeftClick?.Invoke(nav, ItemData.PrimaryText);
                    return;
                }
            }

            if (ItemData.Kind is DetailRowKind.SharedModelSet && ItemData.SharedModelRow is { Set: { } sharedSet }) {
                if (ItemData.SharedModelItemId != 0)
                    OnSharedModelItemLeftClick?.Invoke(ItemData.SharedModelItemId, sharedSet);
                else
                    OnSharedModelSetLeftClick?.Invoke(sharedSet);
                return;
            }

            if (ItemData.Kind is DetailRowKind.SourceDuty && ItemData.NavigateTarget is { } nav2 && nav2.TerritoryTypeId != 0) {
                OnSourceMapFlagLeftClick?.Invoke(nav2, ItemData.PrimaryText);
                return;
            }
        }

        if (!eventData->IsRightClick)
            return;

        if (ItemData.Kind is DetailRowKind.Cost && ItemData.NavigateTarget is not null) {
            OnSourceHeaderRightClick?.Invoke(0, ItemData.NavigateTarget);
            return;
        }

        if (ItemData.Kind is DetailRowKind.Piece or DetailRowKind.Cost && ItemData.ItemId != 0) {
            OnItemRightClick?.Invoke(ItemData.ItemId);
            return;
        }

        if (ItemData.Kind is DetailRowKind.JournalHeader && ItemData.NavigateTarget is not null) {
            OnSourceHeaderRightClick?.Invoke(0, ItemData.NavigateTarget);
            return;
        }

        if (ItemData.Kind is DetailRowKind.SourceDuty
            && (ItemData.ContentFinderConditionId != 0 || ItemData.NavigateTarget is not null))
            OnSourceHeaderRightClick?.Invoke(ItemData.ContentFinderConditionId, ItemData.NavigateTarget);
    }

    private sealed class RowFramedIcon {
        private const float FrameOutset = 8f;
        private static readonly NodeFlags VisibleFlags = NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.EmitsEvents;

        public readonly IconImageNode Image;
        public readonly ImageNode Frame;

        public RowFramedIcon() {
            Image = new IconImageNode {
                FitTexture = true,
                IsVisible = false,
                NodeFlags = VisibleFlags,
            };
            Frame = new ImageNode {
                PartId = 0,
                WrapMode = WrapMode.Stretch,
                IsVisible = false,
                NodeFlags = VisibleFlags,
            };
            IconNodeTextureHelper.LoadIconAFrameTexture(Frame);
        }

        public void AttachTo(NodeBase parent) {
            Frame.AttachNode(parent);
            Image.AttachNode(parent);
        }

        public bool IsVisible {
            get => Image.IsVisible;
            set {
                Image.IsVisible = value;
                Frame.IsVisible = value;
            }
        }

        public void SetItemId(uint itemId) {
            Image.ItemTooltip = itemId;
            Image.IconId = Item.GetRowRef(itemId) is { IsValid: true } row ? (uint)row.Value.Icon : 0;
        }

        public void Layout(Vector2 position, float iconSize) {
            var frameInset = FrameOutset * 0.5f;
            Image.Position = position;
            Image.Size = new Vector2(iconSize);
            Frame.Position = position - new Vector2(frameInset);
            Frame.Size = new Vector2(iconSize + FrameOutset);
        }
    }
}
