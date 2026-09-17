using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class JobPickerDebugRowNode : ResNode {
    private const float RowHeight = 36f;
    private const float ButtonSize = 36f;
    private const float LabelWidth = 132f;

    private readonly TextNode _label;
    private readonly JobPickerButtonNode _button;

    internal JobPickerDebugRowNode(float width) {
        _label = new TextNode {
            Position = Vector2.Zero,
            Size = new Vector2(LabelWidth, RowHeight),
            FontType = FontType.Jupiter,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.Left,
            TextColor = ColourPalette.HeadingGrey,
            String = "Class/Job",
            TextFlags = TextFlags.Emboss,
        };
        _label.AttachNode(this);

        _button = new JobPickerButtonNode {
            Position = new Vector2(LabelWidth, 0f),
        };
        _button.AttachNode(this);

        Size = new Vector2(width, RowHeight);
    }

    internal void Relayout(float width) {
        Size = new Vector2(width, RowHeight);
    }
}
