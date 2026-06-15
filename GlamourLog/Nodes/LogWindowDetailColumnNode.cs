using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed class LogWindowDetailColumnNode : ResNode {
    public DetailRowsListNode List { get; }

    public LogWindowDetailColumnNode(Vector2 size) {
        Size = size;
        List = new DetailRowsListNode {
            Position = Vector2.Zero,
            OptionsList = [],
        };
        List.AttachNode(this);
        List.Size = size;
    }

    public void SyncRowWidths() => List.SyncRowWidths();
    public void PrepareForClose() => List.PrepareForClose();
}
