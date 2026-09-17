using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class DebugActionButtonNode : ResNode {
    private const float RowHeight = 28f;

    private readonly TextButtonNode _button;

    public DebugActionButtonNode(float width, string label, System.Action onClick) {
        _button = new TextButtonNode {
            Size = new Vector2(width, RowHeight),
            String = label,
            OnClick = onClick,
        };
        _button.LabelNode.FontType = FontType.Axis;
        _button.LabelNode.FontSize = 12;
        _button.LabelNode.LineSpacing = 12;
        _button.LabelNode.TextColor = ColourPalette.PrimaryWhite;
        _button.AttachNode(this);

        Size = new Vector2(width, RowHeight);
    }

    internal void Relayout(float width) {
        _button.Size = new Vector2(width, RowHeight);
        Size = new Vector2(width, RowHeight);
    }
}
