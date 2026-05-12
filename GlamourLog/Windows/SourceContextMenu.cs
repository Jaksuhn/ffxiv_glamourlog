using clib.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit;
using ContextMenu = KamiToolKit.ContextMenu.ContextMenu;

namespace GlamourLog;

internal static unsafe class SourceContextMenu {
    public static void Open(NativeAddon owner, uint contentFinderConditionId, SourceNavigateTarget? navigateTarget, ContextMenu menu) {
        if (contentFinderConditionId == 0 && navigateTarget is null)
            return;

        GlamourLogAgentContext.AttachContextMenuTo(owner);
        menu.Clear();

        if (navigateTarget is { TerritoryTypeId: var territoryId, WorldPosition: var pos } && territoryId != 0) {
            menu.AddItem("Navigate to location", () => {
                Svc.Automation.Start(new NavigateToTerritoryPositionTask(territoryId, pos));
            });
        }

        if (contentFinderConditionId != 0) {
            menu.AddItem(Addon.GetRow(15890).Text, () => AgentContentsFinder.Instance()->OpenRegularDuty(contentFinderConditionId));
            menu.AddItem($"{Addon.GetRow(9663).Text} ({Addon.GetRow(1145).Text})", () => {
                if (ContentFinderCondition.GetRowRef(contentFinderConditionId) is { IsValid: true, Value: var cfc })
                    cfc.QueueDuty(levelSync: true);
            });
            menu.AddItem($"{Addon.GetRow(9663).Text} ({Addon.GetRow(10008).Text})", () => {
                if (ContentFinderCondition.GetRowRef(contentFinderConditionId) is { IsValid: true, Value: var cfc })
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
}
