using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed class CompactWindowNode : WindowNode {
    private const float CompactHeaderHeight = 16f;

    public override float HeaderHeight => CompactHeaderHeight;

    public override Vector2 ContentSize
        => new(BackgroundImageNode.Width, BackgroundImageNode.Height - HeaderHeight);

    public override Vector2 ContentStartPosition
        => new(BackgroundImageNode.X, BackgroundImageNode.Y + HeaderHeight);

    public override void SetTitle(string title, string? subtitle = null) {
        base.SetTitle(title, subtitle);
        TitleNode.IsVisible = false;
        SubtitleNode.IsVisible = false;
        DividingLineNode.IsVisible = false;
    }
}
