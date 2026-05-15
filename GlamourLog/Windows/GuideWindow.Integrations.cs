using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog;

public partial class GuideWindow {
    private static readonly GuidePage Integrations = new() {
        CategoryTitle = "Guide",
        SubCategoryTitle = "Plugin Integrations",
        Body = new Lumina.Text.ReadOnly.ReadOnlySeString(
                new SeStringBuilder().Append("GlamourLog does not require other plugins to work, but it can be enhanced with other plugins.\n\n")
                .AddUiForeground(500).AddUiGlow(501).Append("Allagan Tools").AddUiGlowOff().AddUiForegroundOff()
                .Append(" is used in the ").AddUiForeground(710).Append("currencies required").AddUiForegroundOff().Append(" section of ")
                .AddUiForeground(710).Append("set details").AddUiForegroundOff()
                .Append(" to get the amount of currency you have obtained in any inventory on\nyour character. If not installed, this count will default to your standard inventory only.\n\n")
                .AddUiForeground(500).AddUiGlow(501).Append("vnavmesh").AddUiGlowOff().AddUiForegroundOff()
                .Append(" is used in the context menu of ").AddUiForeground(710).Append("Sources and Costs.").AddUiForegroundOff()
                .Append(" If loaded, this context menu entry will navigate you to the relevant source of the item.\n\n")
                .AddUiForeground(500).AddUiGlow(501).Append("AutoDuty").AddUiGlowOff().AddUiForegroundOff()
                .Append(" is used in the context menu of duty ").AddUiForeground(710).Append("Sources").AddUiForegroundOff()
                .Append(" If loaded, this context menu entry will start an AutoDuty loop where your character\nwill run the relevant dungeon until all missing outfit pieces are acquired.")
                .Encode()),
        BodyTextBoxHeight = 420f,
    };
}
