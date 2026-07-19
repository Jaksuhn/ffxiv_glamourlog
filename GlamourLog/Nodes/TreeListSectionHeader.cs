using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes;

public sealed class TreeListSectionHeader : ResNode {
    public NineGridNode BackgroundNode { get; }
    public TextNode LabelNode { get; }

    public ReadOnlySeString String {
        get => LabelNode.String;
        set => LabelNode.String = value;
    }

    public TreeListSectionHeader() {
        BackgroundNode = new SimpleNineGridNode {
            TexturePath = "ui/uld/journal_Separator.tex",
            TextureCoordinates = new Vector2(0f, 0f),
            TextureSize = new Vector2(424f, 24f),
            Size = new Vector2(24f, 24f),
            LeftOffset = 25f,
            RightOffset = 20f,
        };
        BackgroundNode.AttachNode(this);

        LabelNode = new TextNode {
            Position = new Vector2(22f, 1f),
            TextColor = ColorHelper.GetColor(7),
            AlignmentType = AlignmentType.Left,
            FontSize = 12,
            FontType = FontType.Axis,
        };
        LabelNode.AttachNode(this);
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        BackgroundNode.Size = Size;
        LabelNode.Size = new Vector2(Width - 22f, Height);
    }
}
