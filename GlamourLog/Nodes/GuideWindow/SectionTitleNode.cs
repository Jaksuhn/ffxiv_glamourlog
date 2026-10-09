using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class SectionTitleNode : ResNode {
    private const float HeadingHeight = 26f;

    private readonly TextNode _titleText;

    public SectionTitleNode(float width, string title) {
        Size = new Vector2(width, HeadingHeight);

        _titleText = new TextNode {
            Size = new Vector2(width, HeadingHeight),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.TopLeft,
            String = title,
            TextFlags = TextFlags.Emboss,
            TextColor = ThemeColours.Standard.Current,
        };
        ThemeColours.ApplyEmboss(_titleText);
        _titleText.AttachNode(this);
    }
}
