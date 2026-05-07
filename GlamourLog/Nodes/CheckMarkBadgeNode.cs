using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog.Nodes;

public sealed class CheckMarkBadgeNode : SimpleImageNode {
    public CheckMarkBadgeNode() {
        TexturePath = "ui/uld/GetheringNoteBook.tex";
        TextureCoordinates = new Vector2(2f, 90f);
        TextureSize = 22.Vec2();
        Size = 12.Vec2();
    }
}
