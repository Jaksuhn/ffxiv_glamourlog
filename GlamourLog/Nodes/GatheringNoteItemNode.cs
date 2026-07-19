using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class GatheringNoteItemNode : ListButtonNode {
    private const float TextRightMargin = 6f;

    private readonly IconAndStackedTitlesNode _chrome;

    public FramedItemIconNode IconNode => _chrome.Icon;
    public CheckMarkBadgeNode CheckBadge => _chrome.CheckBadge;
    public TextNode TitleNode => _chrome.Title;
    public TextNode SubtitleNode => _chrome.Subtitle;

    public GatheringNoteItemNode(float rowHeight, float iconSize, Vector4? titleColor = null) {
        String = string.Empty;
        // ListButtonNode label unused — custom icon + title/subtitle instead
        LabelNode.IsVisible = false;

        _chrome = new IconAndStackedTitlesNode(iconSize, iconLeftMargin: 0f, titleColor ?? ColourPalette.TitleWhite);
        _chrome.AttachNode(this);

        Height = rowHeight;
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        _chrome.Relayout(Width, Height, TextRightMargin);
    }
}
