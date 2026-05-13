using KamiToolKit.Nodes;
using KamiToolKit.Enums;
using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog.Nodes;

public sealed class FramedItemIconNode : SimpleComponentNode {
    private const float FrameOutset = 8f;

    public readonly IconImageNode IconNode;
    public readonly ImageNode FrameNode;

    public uint ItemId { get; private set; }

    public uint IconId {
        get => IconNode.IconId;
        set => IconNode.IconId = value;
    }

    public FramedItemIconNode(float iconSize = 22f, uint itemId = 0) {
        // simplecomponent defaults can leave nested icon+frame invisible in some atk trees until explicitly flagged
        NodeFlags = FFXIVClientStructs.FFXIV.Component.GUI.NodeFlags.Visible;

        IconNode = new IconImageNode {
            FitTexture = true,
            NodeFlags = FFXIVClientStructs.FFXIV.Component.GUI.NodeFlags.Visible,
        };

        FrameNode = new ImageNode {
            PartId = 0,
            WrapMode = WrapMode.Stretch,
            NodeFlags = FFXIVClientStructs.FFXIV.Component.GUI.NodeFlags.Visible,
        };
        IconNodeTextureHelper.LoadIconAFrameTexture(FrameNode);

        IconNode.AttachNode(this);
        FrameNode.AttachNode(this);

        Size = new Vector2(iconSize, iconSize);
        if (itemId != 0)
            SetItemId(itemId);
    }

    public void SetItemId(uint itemId) {
        ItemId = itemId;
        IconId = Item.GetRowRef(itemId) is { IsValid: true } row ? (uint)row.Value.Icon : 0;
        ItemTooltip = itemId;
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();

        IconNode.Position = Vector2.Zero;
        IconNode.Size = Size;

        var frameInset = FrameOutset * 0.5f;
        FrameNode.Position = new Vector2(-frameInset, -frameInset);
        FrameNode.Size = Size + new Vector2(FrameOutset, FrameOutset);
    }
}
