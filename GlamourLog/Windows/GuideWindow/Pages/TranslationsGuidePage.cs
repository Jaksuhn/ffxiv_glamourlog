using Dalamud.Game.Text.SeStringHandling;
using GlamourLog.Extensions;
using GlamourLog.Nodes.GuideWindow;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class TranslationsGuidePage : IGuidePage {
    private const string CrowdinUrl = "https://crowdin.com/project/ffxiv_glamourlog";

    public string Id => "settings.translations";
    public GuideCategory Category => GuideCategory.Settings;
    public int Order => 1;
    public string Title => UIStrings.Guide_Translations_Title;

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(new Lumina.Text.ReadOnly.ReadOnlySeString(new SeStringBuilder().Markup(UIStrings.Guide_Translations_Intro).Encode())),
            new LinkButtonBlock(UIStrings.Guide_Translations_OpenCrowdin, () => Util.OpenLink(CrowdinUrl)),
        ];

    private sealed record LinkButtonBlock(string Label, System.Action OnClick) : GuideBlock<ActionButtonNode> {
        protected override ActionButtonNode Create(float width) => new(width, Label, OnClick);
        protected override void Relayout(ActionButtonNode node, float width) => node.Relayout(width);
    }
}
