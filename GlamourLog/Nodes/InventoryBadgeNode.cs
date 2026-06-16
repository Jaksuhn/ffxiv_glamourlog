using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes;

public sealed class InventoryBadgeNode : SimpleImageNode {
    public InventoryBadgeNode() {
        TexturePath = "ui/uld/Inventory.tex";
        TextureCoordinates = new Vector2(44f, 220f);
        TextureSize = new Vector2(24f, 24f);
        Size = new Vector2(12f, 12f);
        Color = ColourPalette.Cream;
    }
}
