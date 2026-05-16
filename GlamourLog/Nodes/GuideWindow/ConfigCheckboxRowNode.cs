using GlamourLog.Windows.GuideWindow;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class ConfigCheckboxRowNode : ResNode {
    private const float RowHeight = 24f;
    private const float InfoButtonSize = 24f;
    private const float InfoButtonGap = 4f;

    private readonly CheckboxNode _checkbox;
    private readonly CircleButtonNode _infoButton;
    private readonly CheckboxSettingBlock _setting;

    public ConfigCheckboxRowNode(float width, CheckboxSettingBlock setting) {
        _setting = setting;

        var checkboxWidth = CheckboxWidth(width);

        _checkbox = new CheckboxNode {
            Size = new Vector2(checkboxWidth, RowHeight),
            String = setting.Label,
            IsChecked = setting.Read(),
            OnClick = _setting.Write,
        };
        _checkbox.AttachNode(this);

        _infoButton = new CircleButtonNode {
            Icon = ButtonIcon.Exclamation,
            Size = new Vector2(InfoButtonSize, InfoButtonSize),
            Position = new Vector2(width - InfoButtonSize, 0f),
            TextTooltip = setting.InfoTooltip,
        };
        _infoButton.AttachNode(this);

        Size = new Vector2(width, RowHeight);
    }

    internal void Relayout(float width) {
        var checkboxWidth = CheckboxWidth(width);
        _checkbox.Size = new Vector2(checkboxWidth, RowHeight);
        _infoButton.Position = new Vector2(width - InfoButtonSize, 0f);
        Size = new Vector2(width, RowHeight);
        _checkbox.IsChecked = _setting.Read();
    }

    private static float CheckboxWidth(float width)
        => Math.Max(40f, width - InfoButtonSize - InfoButtonGap);
}
