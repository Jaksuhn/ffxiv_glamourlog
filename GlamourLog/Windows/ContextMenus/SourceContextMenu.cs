using clib.TaskSystem;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;
using System.Threading.Tasks;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace GlamourLog.Windows.ContextMenus;

internal static class SourceContextMenu {
    public static unsafe void Open(NativeAddon owner, uint cfcId, SourceNavigateTarget? navigateTarget, ContextMenu menu) {
        if (cfcId == 0 && navigateTarget is null)
            return;

        GlamourLogAgentContext.AttachContextMenuTo(owner);
        menu.Clear();

        if (navigateTarget is { TerritoryTypeId: not 0 and var territoryId, WorldPosition: var pos } && Svc.Interface.IsPluginLoaded("vnavmesh")) {
            menu.AddItem("Navigate to location", () => {
                Svc.Automation.Start(new NavTo(territoryId, pos));
            });
        }

        if (cfcId != 0) {
            menu.AddItem(Addon.GetRow(15890).Text, () => AgentContentsFinder.Instance()->OpenRegularDuty(cfcId));
            menu.AddItem($"{Addon.GetRow(9663).Text} ({Addon.GetRow(1145).Text})", () => { // Queue (Level Sync)
                if (ContentFinderCondition.GetRowRef(cfcId) is { IsValid: true, Value: var cfc })
                    cfc.QueueDuty(levelSync: true);
            });
            menu.AddItem($"{Addon.GetRow(9663).Text} ({Addon.GetRow(10008).Text})", () => { // Queue (Unrestricted Party)
                if (ContentFinderCondition.GetRowRef(cfcId) is { IsValid: true, Value: var cfc })
                    cfc.QueueDuty(levelSync: false);
            });
            if (Svc.Interface.IsPluginLoaded("AutoDuty")) {
                menu.AddItem("AutoDuty", () => {
                    // TODO
                });
            }
        }

        menu.Open();
    }

    private sealed class NavTo(uint territoryTypeId, Vector3 worldPosition) : TaskBase {
        protected override async Task Execute()
            => await MoveTo(territoryTypeId, worldPosition, MovementConfig.Everything);
    }
}
