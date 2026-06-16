using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

/// <summary>Shared framed-icon + title/subtitle row chrome for set-list and gathering-note rows.</summary>
internal sealed class GlamourIconTitleRowChrome : ResNode {
    public const uint TitleFontSize = 14;
    public const uint SubtitleFontSize = 12;
    private const float TitleY = 2f;
    private const float SubtitleY = 21f;
    private const float IconColumnWidth = 36f;

    private readonly float _iconLeftMargin;
    private readonly float _iconSize;

    public FramedItemIconNode Icon { get; }
    public CheckMarkBadgeNode CheckBadge { get; }
    public TextNode Title { get; }
    public TextNode Subtitle { get; }

    public float TextX => _iconLeftMargin + IconColumnWidth;

    public GlamourIconTitleRowChrome(float iconSize, float iconLeftMargin, Vector4 titleColor) {
        _iconSize = iconSize;
        _iconLeftMargin = iconLeftMargin;

        Icon = new FramedItemIconNode(iconSize);
        CheckBadge = new CheckMarkBadgeNode();
        Title = CreateTitleNode(titleColor);
        Subtitle = CreateSubtitleNode();

        foreach (var child in (NodeBase[])[Icon, CheckBadge, Title, Subtitle])
            child.AttachNode(this);
    }

    public void Relayout(float width, float height, float textRightMargin, float textReserve = 0f) {
        var iconY = (height - _iconSize) * 0.5f;
        Icon.Position = new Vector2(_iconLeftMargin, iconY);
        Icon.Size = new Vector2(_iconSize, _iconSize);
        CheckBadge.Position = new Vector2(_iconLeftMargin + _iconSize - CheckBadge.Size.X - 4f, iconY + _iconSize - CheckBadge.Size.Y);

        var textW = Math.Max(0f, width - TextX - textRightMargin - textReserve);
        Title.Position = new Vector2(TextX, TitleY);
        Subtitle.Position = new Vector2(TextX, SubtitleY);
        Title.Size = new Vector2(textW, 19f);
        Subtitle.Size = new Vector2(textW, 17f);
    }

    private static TextNode CreateTitleNode(Vector4 titleColor)
        => new() {
            FontType = FontType.Axis,
            FontSize = TitleFontSize,
            LineSpacing = TitleFontSize,
            AlignmentType = AlignmentType.BottomLeft,
            TextColor = titleColor,
            TextFlags = TextFlags.Emboss | TextFlags.Ellipsis,
        };

    private static TextNode CreateSubtitleNode()
        => new() {
            FontType = FontType.Axis,
            FontSize = SubtitleFontSize,
            LineSpacing = SubtitleFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = ColourPalette.SubtitleBrown,
            TextFlags = TextFlags.Emboss,
        };
}
