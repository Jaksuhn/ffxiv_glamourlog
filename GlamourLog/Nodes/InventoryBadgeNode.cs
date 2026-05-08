using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog.Nodes;

public sealed class InventoryBadgeNode : SimpleImageNode {
    public InventoryBadgeNode() {
        TexturePath = "ui/uld/Inventory.tex";
        TextureCoordinates = new Vector2(44f, 220f);
        TextureSize = new Vector2(24f, 24f);
        Size = new Vector2(12f, 12f);
        Color = new Vector4(238f / 255f, 225f / 255f, 197f / 255f, 1);
    }
}
