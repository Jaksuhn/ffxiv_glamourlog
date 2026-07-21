using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Services;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace GlamourLog.Windows.LogWindow;

internal static unsafe class SourceMapFlagger {
    internal static void SetFlagAndOpenMap(uint territoryTypeId, Vector3 worldPosition, string label) {
        var agent = AgentMap.Instance();
        if (agent is null)
            return;
        if (TerritoryType.GetRowRef(territoryTypeId) is not { IsValid: true, Value.Map.RowId: var mapId } || mapId == 0)
            return;

        var name = string.IsNullOrWhiteSpace(label) ? "Location" : label;
        agent->FlagMarkerCount = 0; // need to replace last flag
        agent->SetFlagMapMarker(territoryTypeId, mapId, worldPosition);
        agent->OpenMap(mapId, territoryTypeId, name, MapType.QuestLog);
        agent->OpenMap(mapId, territoryTypeId, name);
    }

    internal static void OpenChestMap(uint chestRowId, string label) {
        if (DungeonChestLayout.Instance.TryGet(chestRowId, out var chest))
            chest.OpenMap(label);
    }
}
