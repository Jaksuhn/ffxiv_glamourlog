using System.Threading.Tasks;
using clib.TaskSystem;

namespace GlamourLog;

/// <summary> Automation task: teleport / path to a world position in a territory (clib navmesh pathing).</summary>
internal sealed class NavigateToTerritoryPositionTask(uint territoryTypeId, Vector3 worldPosition) : TaskBase {
    protected override async Task Execute()
        => await MoveTo(territoryTypeId, worldPosition, MovementConfig.GroundMove);
}
