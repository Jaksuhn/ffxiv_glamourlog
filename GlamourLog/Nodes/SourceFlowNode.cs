using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public sealed class SourceFlowNode : ResNode {
    private const int MaxLeftIcons = 4;
    private const int MaxRightIcons = 10;
    private const float IconGap = 4f;
    private const float ArrowGap = 6f;

    private float _iconSize;
    private readonly List<FramedItemIconNode> _leftIcons = [];
    private readonly List<FramedItemIconNode> _rightIcons = [];
    private readonly FlowArrowIconNode _arrow;
    private readonly TextNode _overflow;

    public float IconSize {
        get => _iconSize;
        set {
            _iconSize = value;
            foreach (var ic in _leftIcons) ic.Size = new Vector2(value, value);
            foreach (var ic in _rightIcons) ic.Size = new Vector2(value, value);
        }
    }

    public SourceFlowNode(float iconSize = 24f) {
        _iconSize = iconSize;

        for (var i = 0; i < MaxLeftIcons; i++) {
            var icon = new FramedItemIconNode(iconSize) { IsVisible = false };
            icon.AttachNode(this);
            _leftIcons.Add(icon);
        }

        _arrow = new FlowArrowIconNode { IsVisible = false };
        _arrow.AttachNode(this);

        for (var i = 0; i < MaxRightIcons; i++) {
            var icon = new FramedItemIconNode(iconSize) { IsVisible = false };
            icon.AttachNode(this);
            _rightIcons.Add(icon);
        }

        _overflow = new TextNode {
            IsVisible = false,
            FontType = FontType.Axis,
            FontSize = 11,
            LineSpacing = 11,
            AlignmentType = AlignmentType.Left,
            TextColor = new Vector4(0.65f, 0.65f, 0.65f, 1f),
        };
        // grey +N overflow; emboss off
        _overflow.RemoveTextFlags(TextFlags.Emboss);
        _overflow.AttachNode(this);
    }

    public void Hide() {
        foreach (var ic in _leftIcons) ic.IsVisible = false;
        _arrow.IsVisible = false;
        foreach (var ic in _rightIcons) ic.IsVisible = false;
        _overflow.IsVisible = false;
    }

    public void SetFlow(IReadOnlyList<uint> leftIds, IReadOnlyList<uint> rightIds, int rightOverflow) {
        Hide();

        var leftCount = Math.Min(leftIds.Count, _leftIcons.Count);
        var rightCount = Math.Min(rightIds.Count, _rightIcons.Count);
        if (leftCount == 0 && rightCount == 0)
            return;

        var iconY = (Height - _iconSize) * 0.5f;
        var arrowY = (Height - _arrow.Size.Y) * 0.5f;
        var x = 0f;

        for (var i = 0; i < leftCount; i++) {
            var icon = _leftIcons[i];
            icon.SetItemId(leftIds[i]);
            icon.Size = new Vector2(_iconSize, _iconSize);
            icon.Position = new Vector2(x, iconY);
            icon.IsVisible = true;
            x += _iconSize + IconGap;
        }
        if (leftCount > 0)
            x = x - IconGap + ArrowGap;

        if (leftCount > 0 && rightCount > 0) {
            _arrow.Position = new Vector2(x, arrowY);
            _arrow.IsVisible = true;
            x += _arrow.Size.X + ArrowGap;
        }

        for (var i = 0; i < rightCount; i++) {
            var icon = _rightIcons[i];
            icon.SetItemId(rightIds[i]);
            icon.Size = new Vector2(_iconSize, _iconSize);
            icon.Position = new Vector2(x, iconY);
            icon.IsVisible = true;
            x += _iconSize + IconGap;
        }
        if (rightOverflow > 0) {
            _overflow.String = $"+{rightOverflow}";
            _overflow.Position = new Vector2(x, iconY + 4f);
            _overflow.IsVisible = true;
        }
    }
}
