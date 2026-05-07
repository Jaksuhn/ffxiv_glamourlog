using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class InventorySpaceCounterNode : TextNode {
    public InventorySpaceCounterNode() {
        FontType = FontType.MiedingerMed;
        FontSize = 14;
        LineSpacing = 14;
        AlignmentType = AlignmentType.Right;
        TextColor = 1.Vec4();
        TextOutlineColor = new(240f / 255f, 142f / 255f, 55f / 255f, 1f);
        String = string.Empty;
        AddTextFlags(TextFlags.Edge, TextFlags.Glare);
    }
}
