using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;

namespace GlamourLog;

/// <summary>
/// Native <see cref="AgentContext"/> must have <see cref="AgentContext.OwnerAddon"/> set for the addon that
/// opened a context menu, otherwise the client dismisses it almost immediately (same class of issue as
/// <c>MeterContextMenu.SetAgentOwnerAddon</c> for overlay meters).
/// </summary>
internal static unsafe class GlamourLogAgentContext {
    public static void AttachContextMenuTo(NativeAddon addon) {
        var id = addon.AddonId;
        if (id == 0)
            return;
        AgentContext.Instance()->OwnerAddon = (ushort)id;
    }
}
