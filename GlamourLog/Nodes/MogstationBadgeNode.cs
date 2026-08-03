using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class MogstationBadgeNode : IconImageNode {
    private static readonly Vector2 BadgeSize = new(25f, 25f);

    public MogstationBadgeNode() {
        IconId = 63927;
        FitTexture = true;
        Size = BadgeSize;
        IsVisible = false;
    }

    public void LayoutOverIcon(Vector2 iconPosition, Vector2 iconSize) {
        var glyphCenter = new Vector2(iconPosition.X + iconSize.X - 4f, iconPosition.Y + 4f);
        Position = glyphCenter - Size * 0.5f;
    }
}
