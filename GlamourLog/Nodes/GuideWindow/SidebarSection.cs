using GlamourLog.Windows.GuideWindow;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

/// <summary>One expandable sidebar group: category row plus nested page list.</summary>
internal sealed class SidebarSection {
    public required SidebarCategoryRowNode CategoryRow { get; init; }
    public required VerticalListNode PageList { get; init; }
    public required List<(SidebarPageRowNode Btn, Page Page)> Pages { get; init; }
}
