using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Extensions;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed class SetListRowData {
    public required GlamourSet Set { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required bool IsOwned { get; init; }
    public required bool ShowStorage { get; init; }
    public bool ShowArmoireWarning { get; init; }
    public bool IsSelected { get; init; }
    public GlamourIconNode.IconPart StorageIconPart { get; init; } = GlamourIconNode.IconPart.Dresser;
    public uint IconItemId { get; init; } // row icon uses this item id instead of set token when non-zero
}

internal sealed unsafe class GlamourSetListItemNode : ListItemNode<SetListRowData>, IListItemNode {
    private const float IconLeftMargin = 4f;
    private const float TextX = 36f + IconLeftMargin;
    private const float TextRightMargin = 12f;
    private const float StorageTextReserve = 14f;
    private const uint TitleFontSize = 14;
    private const uint SubFontSize = 12;
    private const float IconSize = 29f;

    public static float ItemHeight => 38f;
    public static Action<GlamourSet>? OnRowRightClick { get; set; }

    private readonly ResNode _iconAnchor;
    private readonly FramedItemIconNode _iconNode;
    private readonly CheckMarkBadgeNode _checkBadge;
    private readonly TextNode _titleNode;
    private readonly TextNode _subtitleNode;
    private readonly GlamourIconNode _storageBadge;
    private readonly ArmoireWarningBadgeNode _armoireWarningBadge;
    private readonly CollisionNode _inputCollision;
    private GlamourIconNode.IconPart _lastStorageIconPart = GlamourIconNode.IconPart.Dresser;

    public GlamourSetListItemNode() {
        // left click uses OnClick for list selection; right uses static handler — disable stock row highlight/selection
        EnableSelection = false;
        EnableHighlight = false;

        _iconAnchor = new ResNode();
        _iconAnchor.AttachNode(this);

        _iconNode = new FramedItemIconNode(IconSize);
        _iconNode.AttachNode(_iconAnchor);

        _checkBadge = new CheckMarkBadgeNode();
        _checkBadge.AttachNode(this);

        _titleNode = new TextNode {
            Position = new Vector2(TextX, 2f),
            FontType = FontType.Axis,
            FontSize = TitleFontSize,
            LineSpacing = TitleFontSize,
            AlignmentType = AlignmentType.BottomLeft,
            TextColor = new(1f, 1f, 1f, 1f),
        };
        _titleNode.AddTextFlags(TextFlags.Emboss, TextFlags.Ellipsis);
        _titleNode.AttachNode(this);

        _subtitleNode = new TextNode {
            Position = new Vector2(TextX, 21f),
            FontType = FontType.Axis,
            FontSize = SubFontSize,
            LineSpacing = SubFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = new(157f / 255f, 131f / 255f, 91f / 255f, 1f),
        };
        _subtitleNode.AddTextFlags(TextFlags.Emboss);
        _subtitleNode.AttachNode(this);

        _storageBadge = new GlamourIconNode(GlamourIconNode.IconPart.Dresser);
        _storageBadge.AttachNode(this);
        _armoireWarningBadge = new ArmoireWarningBadgeNode();
        _armoireWarningBadge.AttachNode(this);

        _inputCollision = new CollisionNode {
            CollisionType = CollisionType.Hit,
            Uses = 0,
            ShowClickableCursor = true,
            NodeFlags = NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.HasCollision |
                        NodeFlags.RespondToMouse | NodeFlags.EmitsEvents,
        };
        _inputCollision.AddDrawFlags(DrawFlags.ClickableCursor);
        _inputCollision.AttachNode(this);
        // full-row hitbox
        _inputCollision.AddEvent(AtkEventType.MouseClick, (_, _, _, _, eventData) => {
            if (eventData is null || ItemData is null)
                return;
            if (eventData->IsLeftClick) {
                OnClick?.Invoke(this);
                return;
            }
            if (eventData->IsRightClick) {
                OnClick?.Invoke(this);
                OnRowRightClick?.Invoke(ItemData.Set);
            }
        });
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();

        var iconY = (Height - IconSize) * 0.5f;
        _iconAnchor.Position = new Vector2(IconLeftMargin, iconY);
        _iconAnchor.Size = new Vector2(IconSize, IconSize);
        _iconNode.Size = new Vector2(IconSize, IconSize);
        _checkBadge.Position = _iconAnchor.Position + new Vector2(
            IconSize - _checkBadge.Size.X - 4f,
            IconSize - _checkBadge.Size.Y);

        _storageBadge.Position = new Vector2(Math.Max(0f, Width - _storageBadge.Size.X - 4f), 2f);
        _armoireWarningBadge.Position = _storageBadge.Position + new Vector2(
            _storageBadge.Size.X - _armoireWarningBadge.Size.X,
            _storageBadge.Size.Y - _armoireWarningBadge.Size.Y);
        UpdateTextBounds();
        _inputCollision.Position = Vector2.Zero;
        _inputCollision.Size = Size;
    }

    protected override void SetNodeData(SetListRowData itemData) {
        var iconItemId = itemData.Set.NonSetCabinetPiece ? itemData.Set.Items[0] : itemData.Set.ItemId;
        _iconNode.SetItemId(iconItemId);
        _inputCollision.ItemTooltip = iconItemId;
        _titleNode.String = itemData.Title;
        _subtitleNode.String = itemData.Subtitle;
        _checkBadge.IsVisible = itemData.IsOwned;

        if (itemData.ShowStorage) {
            if (_lastStorageIconPart != itemData.StorageIconPart) {
                _storageBadge.SetPart(itemData.StorageIconPart);
                _lastStorageIconPart = itemData.StorageIconPart;
            }
            _storageBadge.IsVisible = true;
            _armoireWarningBadge.IsVisible =
                itemData.ShowArmoireWarning &&
                itemData.StorageIconPart is GlamourIconNode.IconPart.Dresser or GlamourIconNode.IconPart.DresserFaded;
        }
        else {
            _storageBadge.IsVisible = false;
            _armoireWarningBadge.IsVisible = false;
        }

        UpdateTextBounds();
    }

    // ListNode.PopulateNodes clears highlight when row refs are rebuilt; re-apply from row data after populate.
    // only promote to selected — clicks use SelectItem for immediate feedback before the next rebuild.
    public override void Update() {
        if (ItemData is { IsSelected: true })
            IsSelected = true;
    }

    private void UpdateTextBounds() {
        var reserve = _storageBadge.IsVisible ? StorageTextReserve : 0f;
        var textW = Math.Max(0f, Width - TextX - TextRightMargin - reserve);
        _titleNode.Size = new Vector2(textW, 19f);
        _subtitleNode.Size = new Vector2(textW, 17f);
    }
}
