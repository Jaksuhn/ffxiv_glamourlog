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
 * ipc for entrusting
 * ipc to check if a dungeon is done
 * smarter AD loops
 * mark hq outfits as owned
 */
public sealed class Plugin(IDalamudPluginInterface dalamud) : IAsyncDalamudPlugin {
    public static Configuration C { get; set; } = null!;
    private static readonly CommandRouter<object> Router = new(BuildRoot());
    private static readonly string[] _commands = ["/glamourlog", "/gl"];

    public async Task LoadAsync(CancellationToken cancellationToken) {
        dalamud.Create<Svc>();
        dalamud.InitCustomClientStructs();
        CLibMain.Init(dalamud, this, CLibModule.All);
        await Svc.Framework.RunOnFrameworkThread(() => KamiToolKitLibrary.Initialize(dalamud));

        C = dalamud.GetPluginConfig() as Configuration ?? new Configuration();
        Svc.Register<CatalogService>();
        Svc.Register<OwnershipService>();
        Svc.Register<CabinetListHandler>();
        Svc.Register<CrystallizeListHandler>();
        Svc.Register<AllaganToolsIpc>();
        Svc.Register<IpcProvider>();
        Svc.Register<ChatAlerts>();

        _commands.ForEach(c => Svc.Commands.AddHandler(c, new(OnCommand) { HelpMessage = $"Toggle the {nameof(GlamourLog)} window" }));
        Svc.Register<WindowsService>();
        Svc.Interface.UiBuilder.OpenMainUi += Svc.Get<WindowsService>().ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi += Svc.Get<WindowsService>().ToggleMainMenu;
    }

    public async ValueTask DisposeAsync() {
        _commands.ForEach(c => Svc.Commands.RemoveHandler(c));

        var windows = Svc.Get<WindowsService>();
        await Svc.Framework.RunOnFrameworkThread(() => {
            Svc.Interface.UiBuilder.OpenMainUi -= windows.ToggleMainWindow;
            Svc.Interface.UiBuilder.OpenConfigUi -= windows.ToggleMainMenu;
        });

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
            .Default(_ => Svc.Get<WindowsService>().ToggleMainWindow())
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
