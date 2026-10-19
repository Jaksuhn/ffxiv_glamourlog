using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class CountersGuidePage : IGuidePage {
    public string Id => "guide.counters";
    public GuideCategory Category => GuideCategory.Guide;
    public int Order => 1;
    public string Title => UIStrings.Guide_Counters_Title;

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(new Lumina.Text.ReadOnly.ReadOnlySeString(
                new SeStringBuilder().Append(UIStrings.Guide_Counters_Completed + "\n")
                .Append(UIStrings.Guide_Counters_Saved + "\n\n")
                .Footnote(UIStrings.Guide_Counters_FootnoteCombined + "\n")
                .Footnote(UIStrings.Guide_Counters_FootnoteMisc).Encode())),
        ];
}
