using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class ConfigCheckboxRowNode : ResNode {
    private const float RowHeight = 24f;
    private const float InfoButtonSize = 24f;
    private const float InfoButtonGap = 4f;

    private readonly CheckboxNode _checkbox;
    private readonly CircleButtonNode _infoButton;
    private readonly Func<bool> _read;

    public ConfigCheckboxRowNode(float width, string label, string infoTooltip, Func<bool> read, Action<bool> write) {
        _read = read;

        var checkboxWidth = CheckboxWidth(width);

        _checkbox = new CheckboxNode {
            Size = new Vector2(checkboxWidth, RowHeight),
            String = label,
            IsChecked = read(),
            OnClick = write,
        };
        _checkbox.AttachNode(this);

        _infoButton = new CircleButtonNode {
            Icon = CircleButtonIcon.Exclamation,
            Size = new Vector2(InfoButtonSize, InfoButtonSize),
            Position = new Vector2(width - InfoButtonSize, 0f),
            TextTooltip = infoTooltip,
        };
        _infoButton.AttachNode(this);

        Size = new Vector2(width, RowHeight);
    }

    internal void Relayout(float width) {
        var checkboxWidth = CheckboxWidth(width);
        _checkbox.Size = new Vector2(checkboxWidth, RowHeight);
        _infoButton.Position = new Vector2(width - InfoButtonSize, 0f);
        Size = new Vector2(width, RowHeight);
        _checkbox.IsChecked = _read();
    }

    private static float CheckboxWidth(float width)
        => Math.Max(40f, width - InfoButtonSize - InfoButtonGap);
}
