using GlamourLog.Windows.GuideWindow;

namespace GlamourLog.Nodes.GuideWindow;

/// <summary>One expandable sidebar group: category row plus page rows in the main nav list.</summary>
internal sealed class SidebarSection {
    public required SidebarCategoryRowNode CategoryRow { get; init; }
    public required List<(SidebarPageRowNode Btn, Page Page)> Pages { get; init; }
}
