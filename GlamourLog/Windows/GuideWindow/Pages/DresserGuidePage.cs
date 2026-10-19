using Dalamud.Game.Text.SeStringHandling;
using GlamourLog.Nodes.GuideWindow;
using KamiToolKit.Enums;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class DresserGuidePage : IGuidePage {
    public string Id => "tweaks.dresser";
    public GuideCategory Category => GuideCategory.Tweaks;
    public int Order => 1;
    public string Title => UIStrings.Guide_Dresser_Title;

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append(UIStrings.Guide_Dresser_Intro)
                        .Encode())),
            new CircleButtonExampleBlock(
                CircleButtonIcon.GearCog,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Markup(UIStrings.Guide_Tweak_Filters)
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis(UIStrings.AddonFilter_HideDeposited)
                        .Encode()),
                TextLeftInset: Constants.IconTextLeft),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis(UIStrings.AddonFilter_Dresser_HideArmoireEligible)
                        .Encode()),
                TextLeftInset: Constants.IconTextLeft),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Emphasis(UIStrings.AddonFilter_Dresser_HideNonOutfit)
                        .Encode()),
                TextLeftInset: Constants.IconTextLeft),
            new CircleButtonExampleBlock(
                CircleButtonIcon.Chest,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Markup(UIStrings.Guide_Dresser_StoreAll)
                        .Encode())),
        ];
}
