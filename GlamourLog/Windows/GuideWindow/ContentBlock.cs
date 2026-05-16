using Lumina.Text.ReadOnly;

namespace GlamourLog.Windows.GuideWindow;

internal abstract record ContentBlock;
internal sealed record GuideTextBlock(ReadOnlySeString Text, float TextLeftInset = 0f, float? TextBoxHeight = null) : ContentBlock;
internal sealed record GuideHeadingBlock(string Title) : ContentBlock;
internal sealed record IconExampleBlock(IconExampleKind Kind, ReadOnlySeString Description, float? TextBoxHeight = null) : ContentBlock;

internal sealed record CheckboxSettingBlock : ContentBlock {
    public string Label { get; }
    public string InfoTooltip { get; }
    internal Func<bool> Read { get; }
    internal Action<bool> Write { get; }

    public CheckboxSettingBlock(string label, string infoTooltip, Func<bool> read, Action<bool> write, System.Action? onChanged = null) {
        Label = label;
        InfoTooltip = infoTooltip;
        Read = read;
        Write = value => {
            write(value);
            C.Save();
            onChanged?.Invoke();
        };
    }
}

internal enum IconExampleKind {
    Checkmark,
    FadedDresser,
    Dresser,
    Armoire,
    WarningDresser,
}
