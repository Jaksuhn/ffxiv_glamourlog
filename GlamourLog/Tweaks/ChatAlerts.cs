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
            return;

        if (message.Message.Payloads.FirstOrDefault(p => p is ItemPayload) is not ItemPayload { Item: var row })
            return;

        var ownership = Svc.Get<OwnershipService>();
        if (ownership.IsItemInArmoire(row.RowId) || ownership.IsItemInGlamourDresser(row.RowId))
            return;

        var catalog = Svc.Get<CatalogService>();
        if (catalog.GlamourSets.Where(s => s.Items.Contains(row.RowId)).ToList() is not { Count: > 0 } sets)
            return;

        var snap = ownership.CaptureSnapshot();
        var ownedBefore = new HashSet<uint>(snap.OwnedItems);
        ownedBefore.Remove(row.RowId);
        var ownedAfter = new HashSet<uint>(ownedBefore) { row.RowId };

        foreach (var set in sets) {
            if (OwnsAllPieces(set, ownedAfter) && !OwnsAllPieces(set, ownedBefore)) {
                if (set.NonSetCabinetPiece) {
                    Svc.Chat.Print(new SeStringBuilder().Append("[GlamourLog] Found misc armoire piece ").Append(SeString.CreateItemLink(set.ItemId)).Append("!").Build());
                }
                else {
                    Svc.Chat.Print(new SeStringBuilder().Append("[GlamourLog] You found the final piece of ").Append(SeString.CreateItemLink(set.ItemId)).Append("!").Build());
                }
                return;
            }
        }

        if (catalog.FindCatalogSetForItem(row.RowId) is { } primarySet)
            Svc.Chat.Print(new SeStringBuilder().Append("[GlamourLog] You found a piece of ").Append(SeString.CreateItemLink(primarySet.ItemId)).Build());
    }

    private static bool OwnsAllPieces(GlamourSet set, HashSet<uint> owned)
        => set.Items.Count > 0 && set.Items.All(owned.Contains);
}
