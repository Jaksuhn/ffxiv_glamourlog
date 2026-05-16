using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace GlamourLog.Windows.LogWindow;

internal static unsafe class SourceMapFlagger {
    /// <summary> Places the player map flag and opens the map for the given territory / world position (quest / vendor style).</summary>
    internal static void SetFlagAndOpenMap(uint territoryTypeId, Vector3 worldPosition, string label) {
        var agent = AgentMap.Instance();
        if (agent is null)
            return;
        if (TerritoryType.GetRowRef(territoryTypeId) is not { IsValid: true, Value.Map.RowId: var mapId } || mapId == 0)
            return;

        var name = string.IsNullOrWhiteSpace(label) ? "Location" : label;
        agent->FlagMarkerCount = 0;
        agent->SetFlagMapMarker(territoryTypeId, mapId, worldPosition);
        agent->OpenMap(mapId, territoryTypeId, name, MapType.QuestLog);
        agent->OpenMap(mapId, territoryTypeId, name);
    }
}
