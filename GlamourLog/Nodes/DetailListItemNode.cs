using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;

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
}

internal sealed unsafe class DetailListItemNode : ListItemNode<DetailListRowData>, IListItemNode {
    public static float ItemHeight => 30f;

    public static Action<uint>? OnPieceLeftClick { get; set; }
    public static Action<uint>? OnItemRightClick { get; set; }
    public static Action<uint, SourceNavigateTarget?>? OnSourceHeaderRightClick { get; set; }
    public static Action<SourceNavigateTarget, string>? OnSourceMapFlagLeftClick { get; set; }
    public static Action<uint>? OnCraftRecipeJournalLeftClick { get; set; }
    public static Action<string, bool>? OnDetailSectionToggle { get; set; } // fired when SectionHeader is toggled
    public static Func<string, bool>? IsDetailSectionCollapsed { get; set; } // restore collapsed state for headers are rebuild if true

    private readonly CollisionNode _inputCollision;
    private readonly TextNode _primary;
    private readonly TextNode _secondary;
    private readonly FramedItemIconNode _icon;
    private readonly TreeComboSectionNode _sectionChrome;
    private readonly TreeListHeaderNode _journalChrome;
    private readonly GlamourIconNode _storageBadge;
    private readonly InventoryBadgeNode _inventoryBadge;
    private readonly ArmoireWarningBadgeNode _armoireWarningBadge;
    private readonly List<FramedItemIconNode> _sourceIcons = [];
    private readonly TextNode _sourceOverflow;
    private readonly SourceFlowNode _arrowFlow;
    private GlamourIconNode.IconPart _lastStoragePart = GlamourIconNode.IconPart.Dresser;

    public DetailListItemNode() {
        // custom collision + HandleClick; SelectableNode row chrome would duplicate/conflict with hit targets below
        EnableSelection = false;
        EnableHighlight = false;

        _icon = new FramedItemIconNode(22f) {
            Position = new Vector2(2f, 4f),
            Size = new Vector2(22f, 22f),
            IsVisible = false,
        };
        _icon.AttachNode(this);

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
            Position = new Vector2(30f, 1f),
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
            Position = new Vector2(30f, 14f),
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
            var icon = new FramedItemIconNode(20f) {
                IsVisible = false,
            };
            icon.AttachNode(this);
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
        _storageBadge.Position = new Vector2(Math.Max(0f, Width - _storageBadge.Size.X - 12f), 3f);
        _inventoryBadge.Position = new Vector2(Math.Max(0f, Width - _inventoryBadge.Size.X - 12f), 3f);
        _armoireWarningBadge.Position = _storageBadge.Position + new Vector2(
            _storageBadge.Size.X - _armoireWarningBadge.Size.X,
            _storageBadge.Size.Y - _armoireWarningBadge.Size.Y);

        _inputCollision.Position = Vector2.Zero;
        _inputCollision.Size = Size;

        // row width from parent updated; pooled ItemData may not re-run SetNodeData same frame
        if (ItemData is not null)
            ApplyDynamicWidth(ItemData);
    }

    private void ApplyDynamicWidth(DetailListRowData data) {
        switch (data.Kind) {
            case DetailRowKind.EmptyHint:
                _primary.Size = new Vector2(Math.Max(20f, Width - 8f), 14f);
                break;
            case DetailRowKind.Piece:
                var pieceTextRightReserve = 8f;
                if (_storageBadge.IsVisible)
                    pieceTextRightReserve = _storageBadge.Size.X + 16f;
                else if (_inventoryBadge.IsVisible)
                    pieceTextRightReserve = _inventoryBadge.Size.X + 16f;
                // atk text + ellipsis needs extra height vs line size; too short ellipsizes with horizontal room left
                _primary.Size = new Vector2(Math.Max(20f, Width - 30f - pieceTextRightReserve), 19f);
                break;
            case DetailRowKind.Cost:
                _primary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);
                _secondary.Size = new Vector2(Math.Max(20f, Width - 54f), 14f);
                break;
            case DetailRowKind.SourceChest:
                float iconOriginX;
                if (data.SourceIconsOnly)
                    iconOriginX = 4f;
                else if (data.PrimaryText.Length > 0 && data.SecondaryText.Length == 0) {
                    const float dutyChestLabelWidth = 56f;
                    _primary.Size = new Vector2(Math.Min(dutyChestLabelWidth, Math.Max(36f, Width - 72f)), 14f);
                    iconOriginX = 6f + dutyChestLabelWidth;
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
                            icon.Size = new Vector2(iconSize, iconSize);
                            icon.Position = new Vector2(iconOriginX + i * (iconSize + iconGap), iconY);
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
        _icon.IsVisible = false;
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

        _inputCollision.ItemTooltip = 0;
        _inputCollision.TextTooltip = string.Empty;
        _inputCollision.IsVisible = true;

        _primary.IsVisible = true;
        _primary.String = itemData.PrimaryText;
        _secondary.String = itemData.SecondaryText;
        _primary.FontSize = 12;
        _primary.LineSpacing = 12;
        _primary.TextColor = ImGuiColors.DalamudWhite;
        _primary.Position = new Vector2(30f, 1f);
        _secondary.Position = new Vector2(30f, 14f);
        _inputCollision.CollisionType = CollisionType.Hit;
        _inputCollision.ShowClickableCursor = false;

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
                _primary.Position = new Vector2(4f, 7f);
                _primary.TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f);
                break;
            case DetailRowKind.Piece:
                var itemRow = Item.GetRow(itemData.ItemId);
                _icon.SetItemId(itemData.ItemId);
                _icon.IsVisible = true;
                _primary.Position = new Vector2(30f, 8f);
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
                var currencyRow = Item.GetRow(itemData.ItemId);
                _icon.SetItemId(itemData.ItemId);
                _icon.IsVisible = true;
                _primary.Position = new Vector2(30f, 1f);
                _primary.TextColor = ColorHelper.GetColor(currencyRow.AtkUiRarityColorId);
                _secondary.IsVisible = true;
                _secondary.Position = new Vector2(30f, 14f);
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
                else if (itemData.PrimaryText.Length > 0 && itemData.SecondaryText.Length == 0) {
                    _primary.String = itemData.PrimaryText;
                    _primary.IsVisible = true;
                    _primary.Position = new Vector2(6f, (ItemHeight - 12f) * 0.5f);
                    _primary.FontSize = 12;
                    _primary.LineSpacing = 12;
                    _primary.TextColor = ImGuiColors.DalamudWhite;
                    _secondary.IsVisible = false;
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
        }

        ApplyDynamicWidth(itemData);
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
}
