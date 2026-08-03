using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

// https://github.com/MidoriKami/VanillaPlus/blob/ca83f78fa9c89f5231a1053ff0ce7f74f34862ff/VanillaPlus/Features/EnhancedLootWindow/EnhancedLootWindow.cs
public sealed class UnobtainableBadgeNode : IconImageNode {
    public UnobtainableBadgeNode() {
        IconId = 61502;
        FitTexture = true;
        IsVisible = false;
    }

    public void LayoutOverIcon(Vector2 iconPosition, Vector2 iconSize) {
        Size = iconSize;
        Origin = iconSize * 0.5f;
        Scale = new Vector2(0.80f, 0.80f);
        Position = iconPosition;
    }
}
