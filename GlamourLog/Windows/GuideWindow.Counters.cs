using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog;

public partial class GuideWindow {
    private static readonly GuidePage GuideCounters = new() {
        CategoryTitle = "Guide",
        SubCategoryTitle = "Counters",
        Body = new Lumina.Text.ReadOnly.ReadOnlySeString(
                new SeStringBuilder()
                .Append("The bottom-right of the glamour log displays two numbers.\n")
                .Append("The top number is the amount of sets you have completed\n")
                .Append("out of the total number of sets available in the game.\n\n")
                .Append("The bottom number is the amount of glamour dresser slots\n")
                .Append("you have\u00A0saved by having these pieces stored as outfits\n")
                .Append("instead of being loose inside the dresser.\n")
                .AddUiForeground(500).AddUiGlow(501).Append("※Completed counter combines sets in the dresser and the armoire").AddUiGlowOff().AddUiForegroundOff().Encode()),
        BodyTextBoxHeight = 220f,
    };
}
