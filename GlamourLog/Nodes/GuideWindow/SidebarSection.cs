using GlamourLog.Windows.GuideWindow;

namespace GlamourLog.Nodes.GuideWindow;

// one expandable guide sidebar group (category header + its page buttons)
internal sealed class SidebarSection {
    public required SidebarCategoryRowNode CategoryRow { get; init; }
    public required List<(SidebarPageRowNode Btn, Page Page)> Pages { get; init; }
}
