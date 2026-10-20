using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class ChatAlertsGuidePage : IGuidePage {
    public string Id => "tweaks.chat-alerts";
    public GuideCategory Category => GuideCategory.Tweaks;
    public int Order => 2;
    public string Title => UIStrings.Guide_ChatAlerts_Title;

    private const string LinkMarker = "\u0001";

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append(UIStrings.Guide_ChatAlerts_Intro).Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    AppendAlertWithLink(ExampleLoot(32597), string.Format(UIStrings.ChatAlert_SetProgress, 3, 5, LinkMarker), 51550)
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    AppendAlertWithLink(ExampleLoot(32622), string.Format(UIStrings.ChatAlert_FinalPiece, LinkMarker), 51550)
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    ExampleLoot(50933)
                        .Append(" ")
                        .Append(UIStrings.ChatAlert_ArmoireItem)
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append(UIStrings.Guide_ChatAlerts_AlreadyOwned)
                        .Encode())),
        ];

    // mimics the game's loot notice, which is itself localized by the client
    private static SeStringBuilder ExampleLoot(uint itemId)
        => AppendWithLink(new SeStringBuilder(), string.Format(UIStrings.Guide_ChatAlerts_ExampleLoot, LinkMarker), itemId);

    private static SeStringBuilder AppendAlertWithLink(SeStringBuilder builder, string text, uint itemId)
        => AppendWithLink(builder.Append(" "), text, itemId);

    private static SeStringBuilder AppendWithLink(SeStringBuilder builder, string text, uint itemId) {
        var parts = text.Split(LinkMarker, 2);
        builder.Append(parts[0]);
        if (parts.Length == 2)
            builder.Append(SeString.CreateItemLink(itemId)).Append(parts[1]);
        return builder;
    }
}
