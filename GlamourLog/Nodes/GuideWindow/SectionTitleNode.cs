using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class SectionTitleNode : ResNode {
    private const float HeadingHeight = 26f;
    private static readonly Vector4 TextColor = ColourPalette.Cream;

    private readonly TextNode _titleText;

    public SectionTitleNode(float width, string title) {
        Size = new Vector2(width, HeadingHeight);

        _titleText = new TextNode {
            Size = new Vector2(width, HeadingHeight),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = TextColor,
            String = title,
            TextFlags = TextFlags.Emboss
        };
        _titleText.AttachNode(this);
    }
}
