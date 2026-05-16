using System.Reflection;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

// detach without disposing for scroll rebuilds
internal static class VerticalListEject {
    private static readonly FieldInfo NodeListField =
        typeof(LayoutListNode).GetField("NodeList", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Eject(VerticalListNode list, NodeBase node) {
        var nodeList = (List<NodeBase>)NodeListField.GetValue(list)!;
        if (!nodeList.Remove(node))
            return;

        node.DetachNode();
        list.RecalculateLayout();
    }

    internal static void EjectAll(VerticalListNode list, IReadOnlyList<NodeBase> nodes) {
        foreach (var node in nodes)
            Eject(list, node);
    }
}
