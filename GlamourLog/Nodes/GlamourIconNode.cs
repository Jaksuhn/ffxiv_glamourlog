using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes;

public sealed class GlamourIconNode : SimpleImageNode {
    public GlamourIconNode(IconPart part) {
        TexturePath = "ui/uld/ItemDetailPutIn.tex";
        TextureSize = 18.Vec2();
        Size = 12.Vec2();
        SetPart(part);
    }

    public void SetPart(IconPart part)
        => TextureCoordinates = part switch {
            IconPart.Armoire => new(36, 18),
            IconPart.Dresser => new(18, 18),
            IconPart.ArmoireFaded => new(36, 0),
            IconPart.DresserFaded => new(18, 0),
            _ => throw new NotImplementedException(),
        };

    public enum IconPart {
        Armoire,
        Dresser,
        ArmoireFaded,
        DresserFaded,
    }
}
