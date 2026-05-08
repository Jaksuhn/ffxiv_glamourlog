using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog.Nodes;

/// <summary>Warning marker for dresser items that can go in the armoire</summary>
public sealed class ArmoireWarningBadgeNode : SimpleImageNode {
    public ArmoireWarningBadgeNode() {
        TexturePath = "ui/uld/IconA_Frame.tex";
        TextureCoordinates = new Vector2(378f, 114f);
        TextureSize = new Vector2(18f, 18f);
        Size = new Vector2(4.5f, 4.5f);
    }
}
