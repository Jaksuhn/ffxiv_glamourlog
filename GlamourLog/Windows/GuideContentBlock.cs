using Dalamud.Game.Text.SeStringHandling;
using Lumina.Text.ReadOnly;

namespace GlamourLog;

internal abstract record GuideContentBlock;

internal sealed record GuideTextBlock(ReadOnlySeString Text, float TextLeftInset = 0f, float? TextBoxHeight = null)
    : GuideContentBlock;

internal sealed record GuideHeadingBlock(string Title) : GuideContentBlock;

internal sealed record GuideIconExampleBlock(GuideIconExampleKind Kind, ReadOnlySeString Description, float? TextBoxHeight = null)
    : GuideContentBlock;

internal enum GuideIconExampleKind {
    Checkmark,
    FadedDresser,
    Dresser,
    Armoire,
    WarningDresser,
}
