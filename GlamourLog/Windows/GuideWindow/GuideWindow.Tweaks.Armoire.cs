using Dalamud.Game.Text.SeStringHandling;
using GlamourLog.Nodes.GuideWindow;
using KamiToolKit.Enums;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page TweaksArmoire = new() {
        CategoryTitle = "Tweaks",
        SubCategoryTitle = "Armoire",
        Blocks =
        [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("GlamourLog adds two buttons next to the category arrows on the armoire window.")
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
                        .Emphasis("Hide items in gearsets")
                        .Encode()),
                TextLeftInset: Constants.IconTextLeft),
            new CircleButtonExampleBlock(
                CircleButtonIcon.Chest,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Highlight("Store all")
                        .Append(" stores all eligible items from your inventory into the armoire. Ignores items in gearsets.")
                        .Encode())),
        ],
    };
}
