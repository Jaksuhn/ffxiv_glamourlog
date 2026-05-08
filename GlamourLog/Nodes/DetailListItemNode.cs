using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal enum DetailRowKind {
    SectionHeader,
    JournalHeader,
    EmptyHint,
    Piece,
    Cost,
    SourceDuty,
    SourceChest,
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
}

internal sealed unsafe class DetailListItemNode : ListItemNode<DetailListRowData>, IListItemNode {
    public static float ItemHeight => 30f;

    public static Action<uint>? OnPieceLeftClick { get; set; }
    public static Action<uint>? OnItemRightClick { get; set; }
    public static Action<uint>? OnDutyRightClick { get; set; }

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
    private GlamourIconNode.IconPart _lastStoragePart = GlamourIconNode.IconPart.Dresser;

    public DetailListItemNode() {
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

        for (var i = 0; i < 8; i++) {
            var icon = new FramedItemIconNode(20f) {
                IsVisible = false,
            };
            icon.AttachNode(this);
            _sourceIcons.Add(icon);
        }

        _inputCollision = new CollisionNode {
            CollisionType = CollisionType.Hit,
            Uses = 0,
        };
        _inputCollision.ShowClickableCursor = true;
        _inputCollision.AddDrawFlags(DrawFlags.ClickableCursor);
        _inputCollision.AddEvent(AtkEventType.MouseClick, (_, _, _, _, eventData) => HandleClick(eventData));
        _inputCollision.AttachNode(this);
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

        _primary.String = itemData.PrimaryText;
        _secondary.String = itemData.SecondaryText;
        _primary.FontSize = 12;
        _primary.LineSpacing = 12;
        _primary.TextColor = ImGuiColors.DalamudWhite;
        _primary.Position = new Vector2(30f, 1f);
        _secondary.Position = new Vector2(30f, 14f);
        _inputCollision.ShowClickableCursor = false;

        switch (itemData.Kind) {
            case DetailRowKind.SectionHeader:
                _primary.String = string.Empty;
                _sectionChrome.IsVisible = true;
                _sectionChrome.String = itemData.PrimaryText;
                _inputCollision.ShowClickableCursor = false;
                break;
            case DetailRowKind.JournalHeader:
                _primary.String = string.Empty;
                _journalChrome.IsVisible = true;
                _journalChrome.String = itemData.PrimaryText;
                break;
            case DetailRowKind.EmptyHint:
                _primary.Position = new Vector2(4f, 7f);
                _primary.TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f);
                break;
            case DetailRowKind.Piece:
                var itemRow = Item.GetRow(itemData.ItemId);
                _icon.SetItemId(itemData.ItemId);
                _icon.IsVisible = true;
                // Piece rows are single-line; keep item names vertically centered.
                _primary.Position = new Vector2(30f, 6f);
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
                var pieceTextRightReserve = (_storageBadge.IsVisible || _inventoryBadge.IsVisible) ? (_storageBadge.Size.X + 16f) : 8f;
                _primary.Size = new Vector2(Math.Max(20f, Width - 30f - pieceTextRightReserve), 16f);
                _inputCollision.ShowClickableCursor = true;
                break;
            case DetailRowKind.Cost:
                var currencyRow = Item.GetRow(itemData.ItemId);
                _icon.SetItemId(itemData.ItemId);
                _icon.IsVisible = true;
                _primary.TextColor = ColorHelper.GetColor(currencyRow.AtkUiRarityColorId);
                _secondary.IsVisible = true;
                _inputCollision.ShowClickableCursor = true;
                break;
            case DetailRowKind.SourceDuty:
                _primary.String = string.Empty;
                _journalChrome.IsVisible = true;
                _journalChrome.String = itemData.PrimaryText;
                _inputCollision.ShowClickableCursor = true;
                break;
            case DetailRowKind.SourceChest:
                _primary.Position = new Vector2(4f, 7f);
                if (itemData.SourceItemIds is { Count: > 0 } sourceItems) {
                    var count = Math.Min(_sourceIcons.Count, sourceItems.Count);
                    var iconSize = 20f;
                    var originX = 172f;
                    var iconY = (ItemHeight - iconSize) * 0.5f;
                    var iconGap = 4f;
                    for (var i = 0; i < count; i++) {
                        var icon = _sourceIcons[i];
                        icon.SetItemId(sourceItems[i]);
                        icon.Size = new Vector2(iconSize, iconSize);
                        icon.Position = new Vector2(originX + i * (iconSize + iconGap), iconY);
                        icon.IsVisible = true;
                    }
                }
                break;
        }
    }

    private void HandleClick(AtkEventData* eventData) {
        if (eventData is null || ItemData is null)
            return;

        if (eventData->IsLeftClick && ItemData.Kind is DetailRowKind.Piece && ItemData.ItemId != 0) {
            OnPieceLeftClick?.Invoke(ItemData.ItemId);
            return;
        }

        if (!eventData->IsRightClick)
            return;

        if ((ItemData.Kind is DetailRowKind.Piece or DetailRowKind.Cost) && ItemData.ItemId != 0) {
            OnItemRightClick?.Invoke(ItemData.ItemId);
            return;
        }

        if (ItemData.Kind is DetailRowKind.SourceDuty && ItemData.ContentFinderConditionId != 0)
            OnDutyRightClick?.Invoke(ItemData.ContentFinderConditionId);
    }
}
