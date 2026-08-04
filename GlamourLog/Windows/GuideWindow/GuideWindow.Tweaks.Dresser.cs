using Dalamud.Game.Text.SeStringHandling;
using GlamourLog.Nodes.GuideWindow;
using KamiToolKit.Enums;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page TweaksDresser = new() {
        CategoryTitle = "Tweaks",
        SubCategoryTitle = "Dresser",
        Blocks =
        [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("GlamourLog adds two buttons above the category arrows on the item deposit window.")
                        .Encode())),
            new CircleButtonExampleBlock(
                CircleButtonIcon.GearCog,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Highlight("Filters")
                        .Append(" opens a filter window to hide items that match some criteria:")
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis("Hide already deposited items")
                        .Encode()),
                TextLeftInset: Constants.IconTextLeft),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis("Hide armoire-eligible items")
                        .Encode()),
                TextLeftInset: Constants.IconTextLeft),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis("Hide non-outfit items")
                        .Encode()),
                TextLeftInset: Constants.IconTextLeft),
            new CircleButtonExampleBlock(
                CircleButtonIcon.Chest,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Highlight("Store all")
                        .Append(" stores all eligible items from your inventory into the dresser. Ignores items in gearsets.")
                        .Encode())),
        ],
    };
}
