using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Windows.GuideWindow;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class CircleButtonSampleRowNode : ResNode {
    private const float ButtonSize = 28f;
    private static readonly Vector4 TextColor = ColourPalette.BodyGrey;

    private readonly ReadOnlySeString _description;
    private readonly CircleButtonNode _button;
    private readonly TextNode _text;
    private float _lastLayoutWidth = -1f;

    public CircleButtonSampleRowNode(float width, CircleButtonIcon icon, ReadOnlySeString description) {
        _description = description;

        _button = new CircleButtonNode {
            Icon = icon,
            Size = new Vector2(ButtonSize, ButtonSize),
            Position = new Vector2(0f, Constants.RowPadTop),
        };
        _button.AttachNode(this);

        _text = new TextNode {
            FontType = FontType.Axis,
            FontSize = Constants.GuideBodyFontSize,
            LineSpacing = Constants.GuideBodyLineSpacing,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = TextColor,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
        };
        _text.RemoveTextFlags(TextFlags.Emboss);
        _text.AttachNode(this);

        Width = width;
        ApplyHeight(width);
    }

    internal void Relayout(float width) => ApplyHeight(width);

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        if (Width > 1f && Math.Abs(Width - _lastLayoutWidth) > 0.5f)
            ApplyHeight(Width);
    }

    private void ApplyHeight(float width) {
        _lastLayoutWidth = width;
        var textX = Constants.IconTextLeft;
        var textW = Math.Max(40f, width - textX);
        var textH = GuideTextLayout.MeasureWrappedHeight(_text, _description, textW);
        var contentH = Math.Max(textH, ButtonSize);
        var rowH = Constants.RowPadTop + contentH + Constants.RowPadBottom;

        _button.Position = new Vector2(0f, Constants.RowPadTop);
        _text.Position = new Vector2(textX, Constants.RowPadTop + Constants.TextTopInset);
        Size = new Vector2(width, rowH);
    }
}
