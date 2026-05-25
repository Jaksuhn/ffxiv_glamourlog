using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page GuideCounters = new() {
        CategoryTitle = "Guide",
        SubCategoryTitle = "Counters",
        Body = new Lumina.Text.ReadOnly.ReadOnlySeString(
                new SeStringBuilder().Append("The bottom-right of the glamour log displays two numbers. The top number is the amount of sets you have completed out of the total number of sets available in the game.\n")
                .Append("The bottom number is the amount of glamour dresser slots you have\nsaved by having these pieces stored as outfits instead of being loose inside the dresser.\n\n")
                .AddUiForeground(500).AddUiGlow(501).Append("※Completed counter combines sets in the dresser and the armoire.\n").AddUiGlowOff().AddUiForegroundOff()
                .AddUiForeground(500).AddUiGlow(501).Append("※\"Misc Armoire\" are not included in either counters.").AddUiGlowOff().AddUiForegroundOff().Encode()),
        BodyTextBoxHeight = 240f,
    };
}
