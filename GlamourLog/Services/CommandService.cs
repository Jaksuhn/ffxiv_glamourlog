using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Tweaks.Cabinet;
using GlamourLog.Tweaks.PrismBox;

namespace GlamourLog.Services;

internal sealed class CommandService : IPluginCommands {
    public string[] Commands { get; } = ["/glamourlog", "/gl"];
    public string HelpMessage => UIStrings.Command_Help;

    public CommandNode<object> Root => field ??= Build();

    private static CommandNode<object> Build()
        => CommandNode<object>.Root(UIStrings.Command_Root)
            .Default(WindowsService.Get().ToggleMainWindow)
            .Sub("stop", UIStrings.Command_Stop, Svc.Automation.Stop)
            .Sub("store", UIStrings.Command_Store, () => {
                if (AtkUnitBase.IsAddonReady("Cabinet"))
                    Svc.Automation.Start(new StoreAllArmoireTask());
                if (AtkUnitBase.IsAddonReady("MiragePrismPrismBoxCrystallize"))
                    Svc.Automation.Start(new StoreAllDresserTask());
            });
}
