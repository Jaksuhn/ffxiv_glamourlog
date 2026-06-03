using clib;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamourLog.Features.Cabinet;
using GlamourLog.Features.PrismBox;
using GlamourLog.Services;
using GlamourLog.Tweaks;
using KamiToolKit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog;

/*
 * TODO
 * Try On should clear the existing items
 * setting: ignore armoire warning if item in dresser is dyed
 * rename glam plates tweak
 * loot window alert for missing pieces (and/or just general inventory change alert)
 * armoire store hide gearset pieces
 */
public sealed class Plugin(IDalamudPluginInterface dalamud) : IAsyncDalamudPlugin {
    public static Configuration C { get; set; } = null!;

    private readonly string[] _commands = ["/glamourlog", "/gl"];

    public async Task LoadAsync(CancellationToken cancellationToken) {
        dalamud.Create<Svc>();
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
        await Svc.Framework.RunOnFrameworkThread(Svc.Register<WindowsService>);
        Svc.Interface.UiBuilder.OpenMainUi += Svc.Get<WindowsService>().ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi += Svc.Get<WindowsService>().ToggleMainMenu;
    }

    public async ValueTask DisposeAsync() {
        _commands.ForEach(c => Svc.Commands.RemoveHandler(c));
        Svc.Interface.UiBuilder.OpenMainUi -= Svc.Get<WindowsService>().ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi -= Svc.Get<WindowsService>().ToggleMainMenu;
        CLibMain.Dispose();
        await Svc.Framework.RunOnFrameworkThread(KamiToolKitLibrary.Dispose);
    }

    private void OnCommand(string command, string arguments) {
        Svc.Get<WindowsService>().ToggleMainWindow();
    }
}
