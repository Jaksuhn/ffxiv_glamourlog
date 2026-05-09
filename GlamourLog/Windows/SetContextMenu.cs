using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;
using KamiToolKit.ContextMenu;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace GlamourLog;

internal static unsafe class SetContextMenu {
    public static void Open(NativeAddon owner, GlamourSet set, ContextMenu menu) {
        GlamourLogAgentContext.AttachContextMenuTo(owner);
        menu.Clear();
        menu.AddItem($"{Addon.GetRow(2426).Text} ({Addon.GetRow(1043).Text})", () => {
            AgentTryon.Instance()->SaveDeleteOutfit = true;
            set.Items.ForEach(i => AgentTryon.TryOn(0, i));
        });
        menu.Open();
    }
}
