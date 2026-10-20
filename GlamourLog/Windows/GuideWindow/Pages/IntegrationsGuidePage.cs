using Dalamud.Game.Text.SeStringHandling;
using GlamourLog.Extensions;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class IntegrationsGuidePage : IGuidePage {
    public string Id => "guide.integrations";
    public GuideCategory Category => GuideCategory.Guide;
    public int Order => 2;
    public string Title => UIStrings.Guide_Integrations_Title;

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append(UIStrings.Guide_Integrations_Intro)
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Markup(UIStrings.Guide_Integrations_AllaganTools)
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Markup(UIStrings.Guide_Integrations_Vnavmesh)
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Markup(UIStrings.Guide_Integrations_AutoDuty)
                        .Encode())),
        ];
}
