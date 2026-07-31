using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class CircleButtonGalleryNode : ResNode {
    private const float ButtonSize = 28f;
    private const float Gap = 6f;

    private readonly List<CircleButtonNode> _buttons = [];
    private float _lastLayoutWidth = -1f;

    public CircleButtonGalleryNode(float width) {
        foreach (var icon in Enum.GetValues<CircleButtonIcon>()) {
            var button = new CircleButtonNode {
                Icon = icon,
                Size = new Vector2(ButtonSize, ButtonSize),
                TextTooltip = icon.ToString(),
            };
            button.AttachNode(this);
            _buttons.Add(button);
        }

        Width = width;
        ApplyLayout(width);
    }

    internal void Relayout(float width) => ApplyLayout(width);

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        if (Width > 1f && Math.Abs(Width - _lastLayoutWidth) > 0.5f)
            ApplyLayout(Width);
    }

    private void ApplyLayout(float width) {
        _lastLayoutWidth = width;

        var x = 0f;
        var y = 0f;
        var rowH = ButtonSize;

        foreach (var button in _buttons) {
            if (x > 0f && x + ButtonSize > width) {
                x = 0f;
                y += rowH + Gap;
            }

            button.Position = new Vector2(x, y);
            x += ButtonSize + Gap;
        }

        var height = _buttons.Count == 0 ? 0f : y + rowH;
        Size = new Vector2(width, height);
    }
}
