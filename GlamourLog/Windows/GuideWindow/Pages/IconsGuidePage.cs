using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class IconsGuidePage : IGuidePage {
    public string Id => "guide.icons";
    public GuideCategory Category => GuideCategory.Guide;
    public int Order => 0;
    public string Title => UIStrings.Guide_Icons_Title;

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new IconExampleBlock(
                IconExampleKind.Checkmark,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Markup(UIStrings.Guide_Icons_Checkmark).Encode())),
            new IconExampleBlock(
                IconExampleKind.Unobtainable,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Markup(UIStrings.Guide_Icons_Unobtainable).Encode())),
            new IconExampleBlock(
                IconExampleKind.FadedDresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Markup(UIStrings.Guide_Icons_FadedDresser).Encode())),
            new IconExampleBlock(
                IconExampleKind.Dresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Markup(UIStrings.Guide_Icons_Dresser).Encode())),
            new IconExampleBlock(
                IconExampleKind.Armoire,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Markup(UIStrings.Guide_Icons_Armoire).Encode())),
            new IconExampleBlock(
                IconExampleKind.WarningDresser,
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder().Markup(UIStrings.Guide_Icons_WarningDresser).Encode())),
        ];
}
