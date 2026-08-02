using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Tweaks.Cabinet;
using GlamourLog.Tweaks.PrismBox;

namespace GlamourLog.Services;

internal sealed class CommandService : IPluginCommands {
    public int InitOrder => 20;

    public string[] Commands { get; } = ["/glamourlog", "/gl"];
    public string HelpMessage => $"Toggle the {nameof(GlamourLog)} window";

    public CommandNode<object> Root => field ??= Build();

    private static CommandNode<object> Build()
        => CommandNode<object>.Root("Glamour Log commands")
            .Default(WindowsService.Get().ToggleMainWindow)
            .Sub("stop", "Cancel any running tasks", Svc.Automation.Stop)
            .Sub("store", "Store all eligible items in your armoire/dresser", () => {
                if (AtkUnitBase.IsAddonReady("Cabinet"))
                    Svc.Automation.Start(new StoreAllArmoireTask());
                if (AtkUnitBase.IsAddonReady("MiragePrismPrismBoxCrystallize"))
                    Svc.Automation.Start(new StoreAllDresserTask());
            });
}
