using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes;

// for items in dresser that can go in armoire
public sealed class ArmoireWarningBadgeNode : SimpleImageNode {
    public ArmoireWarningBadgeNode() {
        TexturePath = "ui/uld/IconA_Frame.tex";
        TextureCoordinates = new Vector2(378f, 114f);
        TextureSize = new Vector2(18f, 18f);
        Size = new Vector2(4.5f, 4.5f);
    }
}
