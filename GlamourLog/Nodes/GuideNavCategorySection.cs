using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

// one parent row + nested sub list per GuideCategoryNav entry
internal sealed class GuideNavCategorySection {
    public required GuideNavParentRowNode Parent { get; init; }
    public required VerticalListNode SubList { get; init; }
    public required List<(GuideNavSubRowNode Btn, GuidePage Page)> Subs { get; init; }
}
