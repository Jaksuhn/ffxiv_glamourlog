using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

// framed icon + title/subtitle layout shared by set-list and gathering-note rows
internal sealed class IconAndStackedTitlesNode : ResNode {
    public const uint TitleFontSize = 14;
    public const uint SubtitleFontSize = 12;
    private const float TitleY = 2f;
    private const float SubtitleY = 21f;
    private const float IconColumnWidth = 36f;

    private readonly float _iconLeftMargin;
    private readonly float _iconSize;

    public FramedItemIconNode Icon { get; }
    public UnobtainableBadgeNode UnobtainableBadge { get; }
    public MogstationBadgeNode MogstationBadge { get; }
    public CheckMarkBadgeNode CheckBadge { get; }
    public TextNode Title { get; }
    public TextNode Subtitle { get; }

    private readonly ThemeColour _titleColour;

    public float TextX => _iconLeftMargin + IconColumnWidth;

    public IconAndStackedTitlesNode(float iconSize, float iconLeftMargin, ThemeColour titleColor) {
        _iconSize = iconSize;
        _iconLeftMargin = iconLeftMargin;
        _titleColour = titleColor;

        Icon = new FramedItemIconNode(iconSize);
        UnobtainableBadge = new UnobtainableBadgeNode();
        MogstationBadge = new MogstationBadgeNode();
        CheckBadge = new CheckMarkBadgeNode();
        Title = CreateTitleNode();
        Subtitle = CreateSubtitleNode();

        Icon.AttachNode(this);
        UnobtainableBadge.AttachNode(this);
        MogstationBadge.AttachNode(this);
        CheckBadge.AttachNode(this);
        Title.TextColor = _titleColour.Current;
        Subtitle.TextColor = ThemeColours.Subtitle.Current;
        ThemeColours.ApplyEmboss(Title);
        ThemeColours.ApplyEmboss(Subtitle);
        Title.AttachNode(this);
        Subtitle.AttachNode(this);
    }

    public void Relayout(float width, float height, float textRightMargin, float textReserve = 0f) {
        var iconY = (height - _iconSize) * 0.5f;
        Icon.Position = new Vector2(_iconLeftMargin, iconY);
        Icon.Size = new Vector2(_iconSize, _iconSize);
        UnobtainableBadge.LayoutOverIcon(Icon.Position, Icon.Size);
        MogstationBadge.LayoutOverIcon(Icon.Position, Icon.Size);
        CheckBadge.Position = new Vector2(_iconLeftMargin + _iconSize - CheckBadge.Size.X - 4f, iconY + _iconSize - CheckBadge.Size.Y);

        var textW = Math.Max(0f, width - TextX - textRightMargin - textReserve);
        Title.Position = new Vector2(TextX, TitleY);
        Subtitle.Position = new Vector2(TextX, SubtitleY);
        Title.Size = new Vector2(textW, 19f);
        Subtitle.Size = new Vector2(textW, 17f);
    }

    private static TextNode CreateTitleNode()
        => new() {
            FontType = FontType.Axis,
            FontSize = TitleFontSize,
            LineSpacing = TitleFontSize,
            AlignmentType = AlignmentType.BottomLeft,
            TextFlags = TextFlags.Emboss | TextFlags.Ellipsis,
        };

    private static TextNode CreateSubtitleNode()
        => new() {
            FontType = FontType.Axis,
            FontSize = SubtitleFontSize,
            LineSpacing = SubtitleFontSize,
            AlignmentType = AlignmentType.TopLeft,
            TextFlags = TextFlags.Emboss,
        };
}
