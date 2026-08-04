using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page Integrations = new() {
        CategoryTitle = "Guide",
        SubCategoryTitle = "Plugin Integrations",
        Blocks =
        [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("GlamourLog does not require other plugins to work, but it can be enhanced with other plugins.")
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis("Allagan Tools")
                        .Append(" is used in the ").Highlight("currencies required").Append(" section of ").Highlight("set details")
                        .Append(" to get the amount of currency you have obtained in any inventory on your character. If not installed, this count will default to your standard inventory only.")
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis("vnavmesh")
                        .Append(" is used in the context menu of ").Highlight("Sources and Costs.")
                        .Append(" If installed, this context menu entry will navigate you to the relevant source of the item.")
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis("AutoDuty")
                        .Append(" is used in the context menu of duty ").Highlight("Sources.")
                        .Append(" If installed, this context menu entry will start an AutoDuty loop where your character will run the relevant dungeon until all missing outfit pieces are acquired.")
                        .Encode())),
        ],
    };
}
