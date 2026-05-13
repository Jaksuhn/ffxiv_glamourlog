using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed class SetListExportControlNode : ResNode {
    private const float ButtonSize = 28f;
    private const float ButtonGap = 2f;

    internal const float LayoutWidth = ButtonGap + ButtonSize; // H-space in header (dir + sort; dropdown extends left)

    internal readonly EnumDropDownNode<GlamourDataExportFormat> ExportDropDown;
    internal readonly CircleButtonNode ExportButton;

    internal SetListExportControlNode() {
        var listOuterWidth = DropDownListOption.OuterWidthForListLabels(DropDownListOption.EnumDescriptions<GlamourDataExportFormat>());
        Size = new Vector2(LayoutWidth, ButtonSize);

        var blockEndX = LayoutWidth + ButtonGap;

        ExportDropDown = new EnumDropDownNode<GlamourDataExportFormat> {
            Position = new Vector2(blockEndX - listOuterWidth, 0f),
            Size = new Vector2(listOuterWidth, ButtonSize),
            Options = [GlamourDataExportFormat.LalaAchievements],
        };
        // icon-only trigger: chrome is circle + popup list; hide stock dropdown chrome and its hitbox (circle toggles)
        ExportDropDown.BackgroundNode.IsVisible = false;
        ExportDropDown.LabelNode.IsVisible = false;
        ExportDropDown.CollapseArrowNode.IsVisible = false;
        ExportDropDown.DisableCollisionNode = true;
        ExportDropDown.AttachNode(this);

        ExportButton = new CircleButtonNode {
            Position = new Vector2(ButtonGap, 0f),
            Size = new Vector2(ButtonSize, ButtonSize),
            Icon = ButtonIcon.Document,
            TextTooltip = "Export data",
            OnClick = () => ExportDropDown.Toggle(),
        };
        ExportButton.AttachNode(this);
    }
}
