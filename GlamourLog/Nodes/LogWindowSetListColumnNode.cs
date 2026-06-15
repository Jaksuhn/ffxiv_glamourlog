using System.ComponentModel;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed unsafe class LogWindowSetListColumnNode : ResNode {
    private const float FilterCogSize = 28f;
    private const float LeftPad = 3f;

    public SetListExportControlNode ExportControl { get; }
    public SetListSortControlNode SortControl { get; }
    public CircleButtonNode FilterButton { get; }
    public GlamourSetListNode List { get; }

    public LogWindowSetListColumnNode(float middleWidth, float setListHeight, float itemSpacing, System.Action openFilterWindow) {
        const float headerHeight = FilterCogSize;
        Size = new Vector2(middleWidth, headerHeight + setListHeight);

        var midHeaderWidth = middleWidth - 8f;
        var filterRelX = middleWidth - FilterCogSize - LeftPad - 4f;
        var sortRelX = filterRelX - 2f - SetListSortControlNode.LayoutWidth;

        var header = new ResNode {
            Position = Vector2.Zero,
            Size = new Vector2(midHeaderWidth, headerHeight),
        };
        header.AttachNode(this);

        ExportControl = new SetListExportControlNode {
            Position = new Vector2(sortRelX - 2f - SetListExportControlNode.LayoutWidth, 0f),
        };
        ExportControl.AttachNode(header);
        ExportControl.ExportDropDown.SelectedOption = GlamourDataExportFormat.LalaAchievements;

        SortControl = new SetListSortControlNode(C.SetListSortDirection) {
            Position = new Vector2(sortRelX, 0f),
        };
        SortControl.AttachNode(header);
        SortControl.SortDropDown.SelectedOption = C.SetListSortMode;

        FilterButton = new CircleButtonNode {
            Icon = CircleButtonIcon.GearCog,
            TextTooltip = "Set list filters",
            Size = new Vector2(FilterCogSize, FilterCogSize),
            Position = new Vector2(filterRelX, 0f),
            OnClick = openFilterWindow,
        };
        FilterButton.AttachNode(header);

        List = new GlamourSetListNode {
            Position = new Vector2(0f, headerHeight),
            OptionsList = [],
            AutoResetScroll = false,
            ItemSpacing = itemSpacing,
        };
        List.AttachNode(this);
        List.Size = new Vector2(middleWidth, setListHeight);
    }

    public void SyncSortDirectionChrome() {
        var btn = SortControl.SortDirectionButton;
        btn.Icon = C.SetListSortDirection == ListSortDirection.Ascending ? CircleButtonIcon.UpArrow : CircleButtonIcon.ArrowDown;
        btn.TextTooltip = C.SetListSortDirection == ListSortDirection.Ascending ? Addon.GetRow(8043).Text : Addon.GetRow(8044).Text;
    }

    public void SyncRowWidths() {
        const float rowWidthInset = 16f;
        var rowWidth = Math.Max(0f, List.Width - rowWidthInset);
        foreach (var node in List.OptionNodes) {
            if (Math.Abs(node.Width - rowWidth) > 0.5f)
                node.Width = rowWidth;
        }
    }

    public void PrepareForClose() {
        List.ScrollBarNode.OnValueChanged = null;
        var bar = (AtkComponentScrollBar*)List.ScrollBarNode;
        bar->IsBeingDragged = false;
        bar->SetContentNode(null, null);
        bar->SetScrollPosition(0);
    }
}
