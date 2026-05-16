using Lumina.Text.ReadOnly;

namespace GlamourLog.Windows.GuideWindow;

internal abstract record ContentBlock;
internal sealed record GuideTextBlock(ReadOnlySeString Text, float TextLeftInset = 0f, float? TextBoxHeight = null) : ContentBlock;
internal sealed record GuideHeadingBlock(string Title) : ContentBlock;
internal sealed record IconExampleBlock(IconExampleKind Kind, ReadOnlySeString Description, float? TextBoxHeight = null) : ContentBlock;

internal enum IconExampleKind {
    Checkmark,
    FadedDresser,
    Dresser,
    Armoire,
    WarningDresser,
}
