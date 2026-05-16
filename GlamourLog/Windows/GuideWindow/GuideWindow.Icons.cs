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
                    new SeStringBuilder().AddUiForeground(710).Append("Checkmark").AddUiForegroundOff()
                    .Append(" is shown on a set icon when every piece is in your glamour dresser or armoire. Inventory does not count.").Encode())),
            new IconExampleBlock(
                IconExampleKind.FadedDresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().AddUiForeground(710).Append("Faded Dresser badge").AddUiForegroundOff()
                    .Append(" is shown on a complete set or owned piece when the item is in your dresser ")
                    .AddUiForeground(500).AddUiGlow(501).Append("not").AddUiGlowOff().AddUiForegroundOff()
                    .Append(" as part of a set.").Encode())),
            new IconExampleBlock(
                IconExampleKind.Dresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().AddUiForeground(710).Append("Dresser badge").AddUiForegroundOff()
                    .Append(" shown on a complete set or owned piece when the item is in your dresser as part of a set.").Encode())),
            new IconExampleBlock(
                IconExampleKind.Armoire,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().AddUiForeground(710).Append("Armoire badge").AddUiForegroundOff()
                    .Append(" is shown on a complete set or owned piece when the item is in your armoire.").Encode())),
            new IconExampleBlock(
                IconExampleKind.WarningDresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().AddUiForeground(710).Append("Dresser warning").AddUiForegroundOff()
                    .Append(" is shown if the item is currently stored in the dresser but could be stored in the armoire. Also applies to sets.").Encode())),
        ],
    };
}
