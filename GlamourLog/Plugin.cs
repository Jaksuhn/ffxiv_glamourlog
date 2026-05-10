using clib;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using GlamourLog.Services;
using KamiToolKit;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog;

/*
 * TODO
 * Try On should clear the existing items
 */
public sealed class Plugin : IAsyncDalamudPlugin {
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    private readonly string[] _commands = ["/glamourlog", "/gl"];
    public static Configuration C { get; set; } = null!;

    public async Task LoadAsync(CancellationToken cancellationToken) {
        PluginInterface.Create<Svc>();
        CLibMain.Init(PluginInterface, this);
        await Svc.Framework.RunOnFrameworkThread(() => KamiToolKitLibrary.Initialize(PluginInterface));

        C = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Svc.Register<CatalogService>();
        Svc.Register<OwnershipService>();

        foreach (var c in _commands) {
            Svc.Commands.AddHandler(c, new CommandInfo(OnCommand) {
                HelpMessage = "Toggle the Glamour Log window.",
            });
        }

        await Svc.Framework.RunOnFrameworkThread(Svc.Register<WindowsService>);
        Svc.Interface.UiBuilder.OpenMainUi += Svc.Get<WindowsService>().ToggleMainWindow;
    }

    public async ValueTask DisposeAsync() {
        _commands.ForEach(c => Svc.Commands.RemoveHandler(c));
        Svc.Interface.UiBuilder.OpenMainUi -= Svc.Get<WindowsService>().ToggleMainWindow;
        Svc.Dispose();
        await Svc.Framework.RunOnFrameworkThread(KamiToolKitLibrary.Dispose);
    }

    private void OnCommand(string command, string arguments) {
        Svc.Get<WindowsService>().ToggleMainWindow();
    }
}
