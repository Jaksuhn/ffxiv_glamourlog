using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class ParagraphNode : ResNode {
    private static readonly Vector4 TextColor = ColourPalette.BodyGrey;

    private readonly ReadOnlySeString _content;
    private readonly float _textLeftInset;
    private readonly TextNode _text;
    private float _lastLayoutWidth = -1f;

    public ParagraphNode(float width, ReadOnlySeString content, float textLeftInset = 0f) {
        _content = content;
        _textLeftInset = textLeftInset;

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
        var textW = Math.Max(40f, width - _textLeftInset);
        var textH = GuideTextLayout.MeasureWrappedHeight(_text, _content, textW);
        var h = textH + Constants.RowPadTop + Constants.RowPadBottom;

        _text.Position = new Vector2(_textLeftInset, Constants.RowPadTop + Constants.TextTopInset);
        Size = new Vector2(width, h);
    }
}
