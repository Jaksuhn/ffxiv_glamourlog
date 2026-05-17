using System.Reflection;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal static class VerticalListEject {
    private static readonly FieldInfo NodeListField =
        typeof(LayoutListNode).GetField("NodeList", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Removes nodes from the list layout only. Caller must <see cref="NodeBase.Dispose"/> each node
    /// (which performs a single detach). Do not call <see cref="NodeBase.DetachNode"/> before dispose.
    /// </summary>
    internal static void RemoveAllWithoutDetach(VerticalListNode list, IReadOnlyList<NodeBase> nodes) {
        var nodeList = (List<NodeBase>)NodeListField.GetValue(list)!;
        foreach (var node in nodes)
            nodeList.Remove(node);

        if (nodes.Count > 0)
            list.RecalculateLayout();
    }

    /// <summary>
    /// Detach without disposing — for scroll rebuilds that re-attach the same nodes.
    /// </summary>
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
