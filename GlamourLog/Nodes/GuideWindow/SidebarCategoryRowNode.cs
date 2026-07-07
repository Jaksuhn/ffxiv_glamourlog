using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class SidebarCategoryRowNode : ListButtonNode {
    public const float RowHeight = 36f;
    private const float LabelX = 12f;

    private static readonly Vector4 TextColor = ColourPalette.TitleWhite;
    private static readonly Vector4 EdgeColor = ColourPalette.SidebarEdgeGold;

    public SidebarCategoryRowNode(string title, System.Action onClick) {
        Height = RowHeight;

        HoverBackgroundNode.IsVisible = false;
        SelectedBackgroundNode.IsVisible = false;

        new SimpleNineGridNode {
            TexturePath = "ui/uld/BgParts.tex",
            TextureCoordinates = new Vector2(105f, 1f),
            TextureSize = new Vector2(36f, 36f),
            LeftOffset = 12f,
            RightOffset = 12f,
            TopOffset = 8f,
            BottomOffset = 8f,
            Size = new Vector2(305f, RowHeight),
        }.AttachNode(LabelNode, NodePosition.BeforeTarget);

        LabelNode.Position = new Vector2(LabelX, 1f);
        LabelNode.Size = new Vector2(305f - LabelX - 8f, RowHeight - 2f);
        LabelNode.FontType = FontType.Axis;
        LabelNode.FontSize = 14;
        LabelNode.LineSpacing = 14;
        LabelNode.AlignmentType = AlignmentType.Left;
        LabelNode.TextColor = TextColor;
        LabelNode.TextOutlineColor = EdgeColor;
        LabelNode.String = title;
        LabelNode.TextFlags = TextFlags.Emboss | TextFlags.Ellipsis;
        LabelNode.RemoveTextFlags(TextFlags.Emboss);

        SidebarListButtonClick.Wire(this, onClick);
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        HoverBackgroundNode.IsVisible = false;
        SelectedBackgroundNode.IsVisible = false;
    }
}
