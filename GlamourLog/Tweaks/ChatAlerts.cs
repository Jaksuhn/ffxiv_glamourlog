using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using GlamourLog.Services;

namespace GlamourLog.Tweaks;

internal class ChatAlerts : IDisposable {
    public ChatAlerts() {
        Svc.Chat.ChatMessage += OnChatMessage;
    }

    public void Dispose() {
        Svc.Chat.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message) {
        if (message.LogKind is not Dalamud.Game.Text.XivChatType.LootNotice)
            return;

        if (message.Message.Payloads.FirstOrDefault(p => p is PlayerPayload) is PlayerPayload { PlayerName: var n } && n != Svc.PlayerState.CharacterName)
            return; // ignore other players

        if (message.Message.Payloads.FirstOrDefault(p => p is ItemPayload) is not ItemPayload { Item: var row })
            return;

        var ownership = Svc.Get<OwnershipService>();
        if (ownership.IsItemInArmoire(row.RowId))
            return;

        var catalog = Svc.Get<CatalogService>();
        if (catalog.GlamourSets.Where(s => s.Items.Contains(row.RowId)).ToList() is not { Count: > 0 } sets)
            return;

        var q = ownership.Query();
        var needingSets = sets.Where(s => q.For(s).Piece(row.RowId) is not { IsStored: true }).ToList();
        if (needingSets.Count == 0)
            return;

        var primarySet = catalog.FindCatalogSetForItem(row.RowId) is { } preferred && needingSets.Contains(preferred) ? preferred : needingSets.OrderBy(s => s.ItemId).First();
        var total = primarySet.Items.Count;
        // inventory events lag behind chat, so simulate before/after owning this piece
        var ownedCountBefore = q.WithOwnedItemOverride(row.RowId, owned: false).For(primarySet).OwnedCount;
        var ownedCount = q.WithOwnedItemOverride(row.RowId, owned: true).For(primarySet).OwnedCount;

        if (ownedCount == total && ownedCountBefore < total) {
            if (primarySet.NonSetCabinetPiece)
                message.Message.Append(" This item can go in your armoire!");
            else
                message.Message.Append(" The final piece of ").Append(SeString.CreateItemLink(primarySet.ItemId)).Append("!");
            return;
        }

        message.Message.Append($" {ownedCount}/{total} of the set ").Append(SeString.CreateItemLink(primarySet.ItemId)).Append("!");
    }
}
