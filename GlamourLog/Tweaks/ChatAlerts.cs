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
                    message.Message.Append(" This item can go in your armoire!");
                }
                else {
                    message.Message.Append(" The final piece of ").Append(SeString.CreateItemLink(set.ItemId)).Append("!");
                }
                return;
            }
        }

        if (catalog.FindCatalogSetForItem(row.RowId) is { } primarySet) {
            var ownedCount = ownership.GetOwnedPieceCountForSet(primarySet, snap with { OwnedItems = ownedAfter });
            message.Message.Append($" {ownedCount}/{primarySet.Items.Count} of the set ").Append(SeString.CreateItemLink(primarySet.ItemId)).Append("!");
        }
    }

    private static bool OwnsAllPieces(GlamourSet set, HashSet<uint> owned)
        => set.Items.Count > 0 && set.Items.All(owned.Contains);
}
