using System.Reflection;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

/// <summary>Detach list children without <see cref="NodeBase.Dispose"/> (safe during scroll rebuilds).</summary>
internal static class GuideListNodeHelper {
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
