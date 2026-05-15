using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed class GuideHeadingBlockNode : ResNode {
    private const float HeadingHeight = 26f;
    private static readonly Vector4 TextColor = new(238f / 255f, 225f / 255f, 197f / 255f, 1f);

    public GuideHeadingBlockNode(float width, string title) {
        Size = new Vector2(width, HeadingHeight);

        var text = new TextNode {
            Size = new Vector2(width, HeadingHeight),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = TextColor,
            String = title,
            TextFlags = TextFlags.Emboss
        };
        text.AttachNode(this);
    }
}
