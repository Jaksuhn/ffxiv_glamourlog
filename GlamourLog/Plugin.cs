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
    public async Task LoadAsync(CancellationToken cancellationToken) {
        PluginInterface.Create<Svc>();
        CLibMain.Init(PluginInterface, this);
        await Svc.Framework.RunOnFrameworkThread(() => KamiToolKitLibrary.Initialize(PluginInterface));

        Svc.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Svc.Catalog = new CatalogService();
        Svc.Ownership = new OwnershipService();

        foreach (var c in _commands) {
            Svc.CommandManager.AddHandler(c, new CommandInfo(OnCommand) {
                HelpMessage = "Toggle the Glamour Log window.",
            });
        }

        await Svc.Framework.RunOnFrameworkThread(() => Svc.Windows = new WindowsService());
        Svc.PluginInterface.UiBuilder.OpenMainUi += Svc.Windows.ToggleMainWindow;
    }

    public async ValueTask DisposeAsync() {
        _commands.ForEach(c => Svc.CommandManager.RemoveHandler(c));
        Svc.PluginInterface.UiBuilder.OpenMainUi -= Svc.Windows.ToggleMainWindow;
        await Svc.Framework.RunOnFrameworkThread(() => {
            Svc.Windows.Dispose();
            KamiToolKitLibrary.Dispose();
        });
        Svc.Catalog.Dispose();
        Svc.Ownership.Dispose();
    }

    private void OnCommand(string command, string arguments) {
        Svc.Windows.ToggleMainWindow();
    }
}
