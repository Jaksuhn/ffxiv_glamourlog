using System.ComponentModel;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class SetListSortControlNode : ResNode {
    private const float DropdownWidth = 150f;
    private const float ButtonSize = 28f;
    private const float ButtonGap = 4f;

    /// <summary> Width of [dropdown][direction][open picker] for header layout. </summary>
    public static float TotalWidth => DropdownWidth + ButtonGap + ButtonSize + ButtonGap + ButtonSize;

    public readonly EnumDropDownNode<GlamourSetSortMode> SortDropDown;
    public readonly CircleButtonNode SortDirectionButton;
    public readonly CircleButtonNode SortButton;

    public SetListSortControlNode(ListSortDirection sortDirection) {
        Size = new Vector2(TotalWidth, ButtonSize);

        SortDropDown = new EnumDropDownNode<GlamourSetSortMode> {
            Position = Vector2.Zero,
            Size = new Vector2(DropdownWidth, ButtonSize),
            Options =
            [
                GlamourSetSortMode.Alphabetical,
                GlamourSetSortMode.ItemLevel,
                GlamourSetSortMode.Patch,
            ],
        };
        SortDropDown.BackgroundNode.IsVisible = false;
        SortDropDown.LabelNode.IsVisible = false;
        SortDropDown.CollapseArrowNode.IsVisible = false;
        SortDropDown.DisableCollisionNode = true;
        SortDropDown.AttachNode(this);

        var dirX = DropdownWidth + ButtonGap;
        SortDirectionButton = new CircleButtonNode {
            Position = new Vector2(dirX, 0f),
            Size = new Vector2(ButtonSize, ButtonSize),
            Icon = sortDirection == ListSortDirection.Ascending ? ButtonIcon.UpArrow : ButtonIcon.ArrowDown,
            TextTooltip = sortDirection == ListSortDirection.Ascending ? Addon.GetRow(8043).Text : Addon.GetRow(8044).Text,
        };
        SortDirectionButton.AttachNode(this);

        SortButton = new CircleButtonNode {
            Position = new Vector2(dirX + ButtonSize + ButtonGap, 0f),
            Size = new Vector2(ButtonSize, ButtonSize),
            Icon = ButtonIcon.Sort,
            TextTooltip = Addon.GetRow(1389).Text, // Sort
            OnClick = () => SortDropDown.Toggle(),
        };
        SortButton.AttachNode(this);
    }
}
