using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class SidebarPageRowNode : ListButtonNode {
    public const float RowHeight = 26f;
    private const float LabelX = 30f;

    private static readonly Vector4 TextColor = ColourPalette.Cream;

    private bool _showSelected;

    public SidebarPageRowNode(string title, System.Action onClick) {
        Height = RowHeight;

        HoverBackgroundNode.IsVisible = false;

        LabelNode.Position = new Vector2(LabelX, 2f);
        LabelNode.FontType = FontType.Axis;
        LabelNode.FontSize = 14;
        LabelNode.LineSpacing = 14;
        LabelNode.AlignmentType = AlignmentType.Left;
        LabelNode.TextColor = TextColor;
        LabelNode.String = title;
        LabelNode.TextFlags = TextFlags.Emboss | TextFlags.Ellipsis;
        SidebarListButtonClick.Wire(this, onClick);
    }

    public void SetPageSelected(bool selected) {
        _showSelected = selected;
        HoverBackgroundNode.IsVisible = false;
        Selected = _showSelected;
        SelectedBackgroundNode.IsVisible = _showSelected;
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();

        HoverBackgroundNode.IsVisible = false;
        SelectedBackgroundNode.IsVisible = _showSelected && IsVisible;

        LabelNode.Position = new Vector2(LabelX, 2f);
        LabelNode.Size = new Vector2(Math.Max(0f, Width - LabelX - 8f), RowHeight - 4f);
    }
}
