using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class ChatAlertsGuidePage : IGuidePage {
    public string Id => "tweaks.chat-alerts";
    public GuideCategory Category => GuideCategory.Tweaks;
    public int Order => 2;
    public string Title => "Chat Alerts";

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("When you loot an item that belongs to a glamour set, outfit progress is appended to the loot notice in chat.").Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("You obtain a ").Append(SeString.CreateItemLink(32597))
                        .Append(". 3/5 of the set ").Append(SeString.CreateItemLink(51550)).Append("!")
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("You obtain a ").Append(SeString.CreateItemLink(32622))
                        .Append(". The final piece of ").Append(SeString.CreateItemLink(51550)).Append("!")
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("You obtain a ").Append(SeString.CreateItemLink(50933))
                        .Append(". This item can go in your armoire!")
                        .Encode())),
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("If you already own the item in storage, the loot notice is left unchanged.")
                        .Encode())),
        ];
}
