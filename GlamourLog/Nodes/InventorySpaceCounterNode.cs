using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class InventorySpaceCounterNode : TextNode {
    public InventorySpaceCounterNode() {
        FontType = FontType.MiedingerMed;
        FontSize = 14;
        LineSpacing = 14;
        AlignmentType = AlignmentType.Right;
        String = string.Empty;
        TextColor = ThemeColours.Counter.Current;
        TextOutlineColor = ThemeColours.CounterEdge.Current;
        AddTextFlags(TextFlags.Edge, TextFlags.Glare);
    }
}
