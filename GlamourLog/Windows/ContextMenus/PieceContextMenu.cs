using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using KamiToolKit;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace GlamourLog.Windows.ContextMenus;

internal static unsafe class PieceContextMenu {
    public static void Open(NativeAddon owner, uint itemId, ContextMenu menu) {
        GlamourLogAgentContext.AttachContextMenuTo(owner);
        var item = Item.GetRow(itemId);
        var itemName = item.Name.ToString();
        menu.Clear();

        menu.AddItem(Addon.GetRow(4379).Text, () => ItemFinderModule.Instance()->SearchForItem(itemId));
        menu.AddItem(Addon.GetRow(4697).Text, () => {
            Svc.Chat.Print(SeString.CreateItemLink(itemId));
            AgentChatLog.Instance()->LinkItem(itemId);
        });
        menu.AddItem(Addon.GetRow(159).Text, () => ImGui.SetClipboardText(itemName));
        menu.AddItem(Addon.GetRow(2426).Text, () => AgentTryon.TryOn(0, itemId));

        if (Recipe.FirstOrNull(r => r.RowId > 0 && r.ItemResult.RowId == itemId) is { RowId: var id }) {
            menu.AddItem(Addon.GetRow(1412).Text, () => AgentRecipeNote.Instance()->OpenRecipeByRecipeId(id));
        }

        menu.Open();
    }
}
