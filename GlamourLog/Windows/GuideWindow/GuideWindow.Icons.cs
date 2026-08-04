using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page Icons = new() {
        CategoryTitle = "Guide",
        SubCategoryTitle = "Icons",
        Blocks =
        [
            new IconExampleBlock(
                IconExampleKind.Checkmark,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Highlight("Checkmark")
                    .Append(" is shown on a set icon when every piece is in your glamour dresser or armoire. Inventory does not count.").Encode())),
            new IconExampleBlock(
                IconExampleKind.Unobtainable,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Highlight("Unobtainable")
                    .Append(" is shown on a set that cannot currently be obtained, e.g. a seasonal event set. These sets are excluded from the completion counters.").Encode())),
            new IconExampleBlock(
                IconExampleKind.FadedDresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Highlight("Faded Dresser badge")
                    .Append(" is shown on a complete set or owned piece when the item is in your dresser ")
                    .Emphasis("not")
                    .Append(" as part of a set.").Encode())),
            new IconExampleBlock(
                IconExampleKind.Dresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Highlight("Dresser badge")
                    .Append(" shown on a complete set or owned piece when the item is in your dresser as part of a set.").Encode())),
            new IconExampleBlock(
                IconExampleKind.Armoire,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Highlight("Armoire badge")
                    .Append(" is shown on a complete set or owned piece when the item is in your armoire.").Encode())),
            new IconExampleBlock(
                IconExampleKind.WarningDresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Highlight("Dresser warning")
                    .Append(" is shown if the item is currently stored in the dresser but could be stored in the armoire. Also applies to sets.").Encode())),
        ],
    };
}
