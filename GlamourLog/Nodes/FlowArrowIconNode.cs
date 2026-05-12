using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog.Nodes;

public sealed class FlowArrowIconNode : SimpleImageNode {
    public FlowArrowIconNode() {
        TexturePath = "ui/uld/Social.tex";
        TextureCoordinates = new Vector2(104f, 102f);
        TextureSize = new Vector2(16f, 16f);
        Size = new Vector2(16f, 16f);
    }
}
