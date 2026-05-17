using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.Cabinet;

internal static unsafe class CabinetState {
    private const string AddonName = "Cabinet";

    internal static bool TryGetOpen(out AddonCabinet* addon, out AgentCabinet* agent) {
        addon = null;
        agent = null;

        var raw = RaptureAtkUnitManager.Instance()->GetAddonByName(AddonName);
        if (raw is null || !raw->IsVisible)
            return false;

        addon = (AddonCabinet*)raw;
        if (addon->ItemList is null)
            return false;

        agent = AgentCabinet.Instance();
        return agent is not null;
    }

    internal static int ActiveRowCount(AddonCabinet* addon) => addon->ItemList->ListLength;

    internal static uint GetItemId(AgentCabinet* agent, int listIndex)
        => agent->ItemCaches[listIndex].Id;

    internal static AtkComponentList* GetItemList(AddonCabinet* addon) => addon->ItemList;
}
