using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace GlamourLog.Windows.ContextMenus;

internal static unsafe class PieceContextMenu {
    public static void Open(NativeAddon owner, uint itemId, ContextMenu menu) {
        GlamourLogAgentContext.AttachContextMenuTo(owner);
        var item = Item.GetRow(itemId);
        var itemName = item.Name.ToString();
        menu.Clear();

        menu.AddItem(Addon.GetRow(4379).Text, () => Svc.Chat.ExecuteCommand($"/isearch {EscapeText(itemName)}"));
        menu.AddItem("Link Item In Chat", () => {
            try { Svc.Chat.Print(SeString.CreateItemLink(itemId, false)); } catch { }
        });
        menu.AddItem(Addon.GetRow(159).Text, () => ImGui.SetClipboardText(itemName));
        menu.AddItem(Addon.GetRow(2426).Text, () => AgentTryon.TryOn(0, itemId));

        if (Recipe.FirstOrNull(r => r.RowId > 0 && r.ItemResult.RowId == itemId) is { RowId: var id }) {
            menu.AddItem("Open Recipe", () => AgentRecipeNote.Instance()->OpenRecipeByRecipeId(id));
        }

        if (!item.IsUntradable) {
            if (Svc.Interface.IsPluginLoaded("MarketBoardPlugin")) {
                menu.AddItem("Open In MarketBoardPlugin", () => Svc.Commands.ProcessCommand($"/pmb {itemId}"));
            }
            if (Svc.Interface.IsPluginLoaded("vmarket")) {
                menu.AddItem("Open In vmarket", () => Svc.Commands.ProcessCommand($"/vmarket {itemId}"));
            }
        }

        menu.Open();
    }

    private static string EscapeText(string text) => $"\"{text.Replace("\"", "\\\"")}\"";
}
