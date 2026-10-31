using Dalamud.Plugin.Ipc;

namespace GlamourLog.Services;

internal sealed class AutoDutyIpc : IPluginService {
    private static readonly (string Key, string Value)[] _configs = [
        ("Loop.Between.Enabled", "true"),
        ("Loop.Between.Actions.[GlamourLoopActionConfig].Enabled", "true"),
        ("Loop.Between.Actions.[ArmoireLoopActionConfig].Enabled", "true"),
        ("Loop.Termination.Enabled", "true"),
        ("Loop.Termination.StopWhenDutyGathered", "true"),
    ];

    private readonly ICallGateSubscriber<string, bool, object> _pluginConfig;
    private readonly ICallGateSubscriber<string, object, object> _setConfig;
    private readonly ICallGateSubscriber<uint, bool> _contentHasPath;
    private readonly ICallGateSubscriber<string, uint, int, bool, object> _runForPlugin;

    public AutoDutyIpc() {
        _pluginConfig = Svc.Interface.GetIpcSubscriber<string, bool, object>("AutoDuty.PluginConfig");
        _setConfig = Svc.Interface.GetIpcSubscriber<string, object, object>("AutoDuty.SetConfig");
        _contentHasPath = Svc.Interface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
        _runForPlugin = Svc.Interface.GetIpcSubscriber<string, uint, int, bool, object>("AutoDuty.RunForPlugin");
    }

    internal void FarmOutfit(uint cfcId) {
        if (cfcId == 0 || ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true, Value.TerritoryType.RowId: > 0 and var territory }) {
            IChatGui.Get().EchoMessage(UIStrings.AutoDuty_InvalidDuty);
            return;
        }

        if (OwnershipService.Get().IsContentComplete(cfcId)) {
            IChatGui.Get().EchoMessage(UIStrings.AutoDuty_AllCollected);
            return;
        }

        if (!_contentHasPath.HasFunction || !_contentHasPath.InvokeFunc(territory)) {
            IChatGui.Get().EchoMessage(UIStrings.AutoDuty_NoPath);
            return;
        }

        if (!_runForPlugin.HasAction || !_pluginConfig.HasAction || !_setConfig.HasAction) {
            IChatGui.Get().EchoError(UIStrings.AutoDuty_SetupFailed);
            return;
        }

        _pluginConfig.InvokeAction(Svc.Interface.Manifest.InternalName, true);
        _configs.ForEach(_setConfig.InvokeAction);
        _runForPlugin.InvokeAction(Svc.Interface.Manifest.InternalName, territory, 0, false);
    }
}
