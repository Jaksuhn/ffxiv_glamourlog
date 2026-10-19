using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class LootWindowGuidePage : IGuidePage {
    public string Id => "tweaks.loot-window";
    public GuideCategory Category => GuideCategory.Tweaks;
    public int Order => 3;
    public string Title => UIStrings.Guide_LootWindow_Title;

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append(UIStrings.Guide_LootWindow_Intro)
                        .Encode())),
            new IconExampleBlock(
                IconExampleKind.Armoire,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Markup(UIStrings.Guide_LootWindow_Armoire)
                        .Encode())),
            new IconExampleBlock(
                IconExampleKind.Dresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Markup(UIStrings.Guide_LootWindow_Dresser)
                        .Encode())),
        ];
}
