using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;

namespace GlamourLog.Windows.ContextMenus;

// AgentContext dismisses unless OwnerAddon points at the atk unit that opened the menu (ktk NativeAddon id)
internal static unsafe class GlamourLogAgentContext {
    public static void AttachContextMenuTo(NativeAddon addon) {
        var id = addon.AddonId;
        if (id == 0)
            return;
        AgentContext.Instance()->OwnerAddon = (ushort)id;
    }
}
