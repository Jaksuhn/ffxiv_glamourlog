using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class SetListSortControlNode : ResNode {
    private const float DropdownWidth = 150f;
    private const float ButtonSize = 28f;

    public readonly EnumDropDownNode<GlamourSetSortMode> SortDropDown;
    public readonly CircleButtonNode SortButton;

    public SetListSortControlNode() {
        Size = new Vector2(DropdownWidth, ButtonSize);

        SortDropDown = new EnumDropDownNode<GlamourSetSortMode> {
            Position = Vector2.Zero,
            Size = new Vector2(DropdownWidth, ButtonSize),
            Options =
            [
                GlamourSetSortMode.AlphabeticalAscending,
                GlamourSetSortMode.ItemLevelDescending,
                GlamourSetSortMode.PatchDescending,
            ],
        };
        SortDropDown.BackgroundNode.IsVisible = false;
        SortDropDown.LabelNode.IsVisible = false;
        SortDropDown.CollapseArrowNode.IsVisible = false;
        SortDropDown.DisableCollisionNode = true;
        SortDropDown.AttachNode(this);

        SortButton = new CircleButtonNode {
            Position = Vector2.Zero,
            Size = new Vector2(ButtonSize, ButtonSize),
            Icon = ButtonIcon.Sort,
            TextTooltip = Addon.GetRow(1389).Text, // Sort
            OnClick = () => SortDropDown.Toggle(),
        };
        SortButton.AttachNode(this);
    }
}
