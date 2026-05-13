using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class GatheringNoteItemNode : ListButtonNode {
    private const float TextX = 36f;
    private const float TextRightMargin = 6f;
    private const uint TitleFontSize = 14;
    private const uint SubFontSize = 12;

    private readonly float _iconSize;

    public readonly FramedItemIconNode IconNode;
    public readonly CheckMarkBadgeNode CheckBadge;
    public readonly TextNode TitleNode;
    public readonly TextNode SubtitleNode;

    public GatheringNoteItemNode(float rowHeight, float iconSize, Vector4 titleColor) {
        _iconSize = iconSize;
        String = string.Empty;
        // ListButtonNode label unused — custom icon + title/subtitle instead
        LabelNode.IsVisible = false;

        IconNode = new FramedItemIconNode(iconSize);
        CheckBadge = new CheckMarkBadgeNode();

        TitleNode = new TextNode {
            Position = new Vector2(TextX, 2f),
            FontType = FontType.Axis,
            FontSize = TitleFontSize,
            LineSpacing = TitleFontSize,
            AlignmentType = AlignmentType.BottomLeft,
            TextColor = titleColor,
        };
        TitleNode.AddTextFlags(TextFlags.Emboss, TextFlags.Ellipsis);

        SubtitleNode = new TextNode {
            Position = new Vector2(TextX, 21f),
            FontType = FontType.Axis,
            FontSize = SubFontSize,
            LineSpacing = SubFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = new(157f / 255f, 131f / 255f, 91f / 255f, 1f),
        };
        SubtitleNode.AddTextFlags(TextFlags.Emboss);

        foreach (var child in (NodeBase[])[IconNode, CheckBadge, TitleNode, SubtitleNode])
            child.AttachNode(this);

        Height = rowHeight;
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();

        if (IconNode is null || CheckBadge is null || TitleNode is null || SubtitleNode is null)
            return;

        var iconY = (Height - _iconSize) * 0.5f;
        IconNode.Position = new Vector2(0f, iconY);
        IconNode.Size = new Vector2(_iconSize, _iconSize);
        CheckBadge.Position = new Vector2(_iconSize - CheckBadge.Size.X - 4f, iconY + _iconSize - CheckBadge.Size.Y);

        var textW = Math.Max(0f, Width - TextX - TextRightMargin);
        TitleNode.Size = new Vector2(textW, 19f);
        SubtitleNode.Size = new Vector2(textW, 17f);
    }
}
