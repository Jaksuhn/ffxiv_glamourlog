using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

// Vertical list that can attach/detach owned rows without disposing them
// collisions are my passion
internal sealed class SidebarNavList : VerticalListNode {
    public void SetManagedNodes(IReadOnlyList<NodeBase> nodes) {
        foreach (var node in NodeList.ToList()) {
            NodeList.Remove(node);
            node.DetachNode();
        }

        foreach (var node in nodes) {
            NodeList.Add(node);
            node.AttachNode(this);
        }

        RecalculateLayout();
    }

    public void ReleaseManagedNodes() {
        foreach (var node in NodeList.ToList()) {
            NodeList.Remove(node);
            node.DetachNode();
        }
    }
}
