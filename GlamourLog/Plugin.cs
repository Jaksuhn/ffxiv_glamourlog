using clib;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using KamiToolKit;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourLog;

public sealed class Plugin : IAsyncDalamudPlugin {
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    private readonly string[] _commands = ["/glamourlog", "/gl"];
    private GlamourLogTracker? _tracker;

    public async Task LoadAsync(CancellationToken cancellationToken) {
        PluginInterface.Create<Svc>();
        CLibMain.Init(PluginInterface, this);

        Svc.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        foreach (var c in _commands) {
            Svc.CommandManager.AddHandler(c, new CommandInfo(OnCommand) {
                HelpMessage = "Toggle the Glamour Log window.",
            });
        }

        await Svc.Framework.RunOnFrameworkThread(() => {
            KamiToolKitLibrary.Initialize(PluginInterface);
            _tracker = new GlamourLogTracker();
        });
    }

    public async ValueTask DisposeAsync() {
        _commands.ForEach(c => Svc.CommandManager.RemoveHandler(c));
        await Svc.Framework.RunOnFrameworkThread(() => {
            _tracker?.Dispose();
            _tracker = null;
            KamiToolKitLibrary.Dispose();
        });
    }

    private void OnCommand(string command, string arguments) {
        if (_tracker is { })
            _tracker.ToggleMainWindow();
        else
            Svc.Chat.Print($"window not available");
    }
}
