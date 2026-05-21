using Dalamud.Game.Chat;
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
        if (message.LogKind is Dalamud.Game.Text.XivChatType.LootNotice) {
            if (message.Message.Payloads.FirstOrDefault(p => p is ItemPayload) is ItemPayload { Item: var row }) {
                if (!Svc.Get<OwnershipService>().IsItemInArmoire(row.RowId) && !Svc.Get<OwnershipService>().IsItemInGlamourDresser(row.RowId, null) && Svc.Get<CatalogService>().GlamourSets.Any(s => s.Items.Contains(row.RowId))) {
                    Svc.Chat.Print("You found an outfit item!");
                }
            }
        }
    }
}
