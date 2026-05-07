using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog.Nodes;

/// <summary>Corner badge for dresser / armoire icons; same pattern as <see cref="CheckMarkBadgeNode" /> (single part, UV crop).</summary>
public sealed class GlamourIconNode : SimpleImageNode {
    public GlamourIconNode(IconPart part) {
        TexturePath = "ui/uld/ItemDetailPutIn.tex";
        TextureSize = 18.Vec2();
        Size = 12.Vec2();
        TextureCoordinates = part switch {
            IconPart.Armoire => new(36, 18),
            IconPart.Dresser => new(18, 18),
            IconPart.ArmoireFaded => new(36, 0),
            IconPart.DresserFaded => new(18, 0),
            _ => throw new NotImplementedException(),
        };
    }

    public enum IconPart {
        Armoire,
        Dresser,
        ArmoireFaded,
        DresserFaded,
    }
}
