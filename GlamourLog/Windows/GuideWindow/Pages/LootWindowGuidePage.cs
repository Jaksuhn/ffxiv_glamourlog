using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class LootWindowGuidePage : IGuidePage {
    public string Id => "tweaks.loot-window";
    public GuideCategory Category => GuideCategory.Tweaks;
    public int Order => 3;
    public string Title => "Loot Window";

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("When the loot window is open, a badge is displayed on item icons for glam pieces you do not already own.")
                        .Encode())),
            new IconExampleBlock(
                IconExampleKind.Armoire,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Highlight("Armoire badge")
                        .Append(" is shown on unowned armoire-eligible items.")
                        .Encode())),
            new IconExampleBlock(
                IconExampleKind.Dresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Highlight("Dresser badge")
                        .Append(" is shown on unowned, non-armoire outfit pieces.")
                        .Encode())),
        ];
}
