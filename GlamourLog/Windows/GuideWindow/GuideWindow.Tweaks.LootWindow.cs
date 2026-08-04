using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page TweaksLootWindow = new() {
        CategoryTitle = "Tweaks",
        SubCategoryTitle = "Loot Window",
        Blocks =
        [
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
        ],
    };
}
