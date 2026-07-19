using FFXIVClientStructs.FFXIV.Component.GUI;
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
    public GlamourIconNode.IconPart StorageIconPart { get; init; } = GlamourIconNode.IconPart.Dresser;
    public uint IconItemId { get; init; } // row icon uses this item id instead of set token when non-zero
}

internal sealed unsafe class GlamourSetListItemNode : ListItemWithFocusNav<SetListRowData>, IListItemNode {
    private const float IconLeftMargin = 4f;
    private const float TextRightMargin = 12f;
    private const float StorageTextReserve = 14f;
    private const float IconSize = 29f;

    public static float ItemHeight => 38f;
    public Action<GlamourSet>? OnRowRightClick { get; set; }

    private readonly IconAndStackedTitlesNode _chrome;
    private readonly GlamourIconNode _storageBadge;
    private readonly ArmoireWarningBadgeNode _armoireWarningBadge;
    private GlamourIconNode.IconPart _lastStorageIconPart = GlamourIconNode.IconPart.Dresser;

    public GlamourSetListItemNode() {
        _chrome = new IconAndStackedTitlesNode(IconSize, IconLeftMargin, ColourPalette.TitleWhite);
        _chrome.AttachNode(this);

        _storageBadge = new GlamourIconNode(GlamourIconNode.IconPart.Dresser);
        _storageBadge.AttachNode(this);
        _armoireWarningBadge = new ArmoireWarningBadgeNode();
        _armoireWarningBadge.AttachNode(this);

        AddEvent(AtkEventType.MouseClick, (_, _, _, _, eventData) => {
            if (eventData is null || ItemData is null || !eventData->IsRightClick)
                return;
            OnRowRightClick?.Invoke(ItemData.Set);
        });
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();

        _chrome.Relayout(
            Width,
            Height,
            TextRightMargin,
            _storageBadge.IsVisible ? StorageTextReserve : 0f);

        _storageBadge.Position = new Vector2(Math.Max(0f, Width - _storageBadge.Size.X - 4f), 2f);
        _armoireWarningBadge.Position = _storageBadge.Position + new Vector2(
            _storageBadge.Size.X - _armoireWarningBadge.Size.X,
            _storageBadge.Size.Y - _armoireWarningBadge.Size.Y);
    }

    protected override void SetNodeData(SetListRowData itemData) {
        var iconItemId = itemData.Set.NonSetCabinetPiece ? itemData.Set.Items[0] : itemData.Set.ItemId;
        _chrome.Icon.SetItemId(iconItemId);
        _chrome.Title.String = itemData.Title;
        _chrome.Subtitle.String = itemData.Subtitle;
        _chrome.CheckBadge.IsVisible = itemData.IsOwned;

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

        _chrome.Relayout(
            Width,
            Height,
            TextRightMargin,
            _storageBadge.IsVisible ? StorageTextReserve : 0f);
    }
}
