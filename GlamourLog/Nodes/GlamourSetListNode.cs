using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed class GlamourSetListNode : ListNode<SetListRowData, GlamourSetListItemNode> {
    public Action<GlamourSet>? OnRowRightClick {
        get => onRowRightClick;
        set {
            onRowRightClick = value;
            SyncRowCallbacks();
        }
    }

    private Action<GlamourSet>? onRowRightClick;

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        SyncRowCallbacks();
    }

    private void SyncRowCallbacks() {
        foreach (var node in OptionNodes)
            node.OnRowRightClick = onRowRightClick;
    }
}
