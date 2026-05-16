using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class ParagraphNode : ResNode {
    private static readonly Vector4 TextColor = new(204f / 255f, 204f / 255f, 204f / 255f, 1f);

    private readonly ReadOnlySeString _content;
    private readonly float _textLeftInset;
    private readonly float _textBoxHeight;
    private readonly TextNode _text;
    private float _lastLayoutWidth = -1f;

    public ParagraphNode(float width, ReadOnlySeString content, float textLeftInset = 0f, float? textBoxHeight = null) {
        _content = content;
        _textLeftInset = textLeftInset;
        _textBoxHeight = textBoxHeight ?? Constants.DefaultGuideBodyTextBoxHeight;

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
        var textH = _textBoxHeight;
        var h = textH + Constants.RowPadTop + Constants.RowPadBottom;

        _text.String = _content;
        _text.Position = new Vector2(_textLeftInset, Constants.RowPadTop + Constants.TextTopInset);
        _text.Size = new Vector2(textW, textH);
        Size = new Vector2(width, h);
    }
}
