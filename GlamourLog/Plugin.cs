using clib;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Features.Cabinet;
using GlamourLog.Features.PrismBox;
using GlamourLog.Services;
using GlamourLog.Tweaks;
using KamiToolKit;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog;

/*
 * TODO
 * setting: ignore armoire warning if item in dresser is dyed (can't really do as dyed info isn't cached in itemfinder
 * rename glam plates tweak
 * loot window alert for missing pieces (and/or just general inventory change alert)
 */
public sealed class Plugin(IDalamudPluginInterface dalamud) : IAsyncDalamudPlugin {
    public static Configuration C { get; set; } = null!;
    private static readonly CommandRouter<object> Router = new(BuildRoot());
    private static readonly string[] _commands = ["/glamourlog", "/gl"];

    public async Task LoadAsync(CancellationToken cancellationToken) {
        dalamud.Create<Svc>();
#if LOCAL_CS
        dalamud.InitCustomClientStructs();
#endif
        CLibMain.Init(dalamud, this, CLibModule.All);
        KamiToolKitLibrary.Initialize(dalamud);

        C = dalamud.GetPluginConfig() as Configuration ?? new Configuration();
        _commands.ForEach(c => Svc.Commands.AddHandler(c, new(OnCommand) { HelpMessage = $"Toggle the {nameof(GlamourLog)} window" }));
        Svc.Interface.UiBuilder.OpenMainUi += WindowsService.Get().ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi += WindowsService.Get().ToggleMainMenu;
    }

    public async ValueTask DisposeAsync() {
        _commands.ForEach(c => Svc.Commands.RemoveHandler(c));
        Svc.Interface.UiBuilder.OpenMainUi -= WindowsService.Get().ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi -= WindowsService.Get().ToggleMainMenu;
        await CLibMain.DisposeAsync();
        await Svc.Framework.RunOnFrameworkThread(KamiToolKitLibrary.Dispose);
    }

    internal static void OnCommand(string command, string arguments) {
        var result = Router.Execute(arguments, null!, _commands[1]);
        if (!result.Success) {
            if (result.Error is not null)
                Svc.Chat.PrintError(result.Error);
            if (result.Usage is not null)
                Svc.Chat.Print(result.Usage);
            return;
        }

        if (result.Help is not null)
            Svc.Chat.Print(result.Help);
    }

    private static CommandNode<object> BuildRoot()
        => CommandNode<object>.Root("Glamour Log commands")
            .Default(_ => WindowsService.Get().ToggleMainWindow())
            .Sub("stop", "Cancel any running tasks", stop => stop
                .Handle((_, _) => Svc.Automation.Stop()))
            .Sub("store", "Store all eligible items in your armoire/dresser", store => store
                .Handle((_, _) => {
                    if (AtkUnitBase.IsAddonReady("Cabinet"))
                        Svc.Automation.Start(new StoreAllArmoireTask());
                    if (AtkUnitBase.IsAddonReady("MiragePrismPrismBoxCrystallize"))
                        Svc.Automation.Start(new StoreAllDresserTask());
                }));
}
