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

    public Task LoadAsync(CancellationToken cancellationToken) {
        PluginInterface.Create<Svc>();
        CLibMain.Init(PluginInterface, this);
        KamiToolKitLibrary.Initialize(PluginInterface);

        Svc.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _tracker = new GlamourLogTracker();

        foreach (var c in _commands) {
            Svc.CommandManager.AddHandler(c, new CommandInfo(OnCommand) {
                HelpMessage = "Toggle the Glamour Log window.",
            });
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() {
        _commands.ForEach(c => Svc.CommandManager.RemoveHandler(c));
        _tracker?.Dispose();
        _tracker = null;
        KamiToolKitLibrary.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnCommand(string command, string arguments) {
        if (_tracker is { })
            _tracker.ToggleMainWindow();
        else
            Svc.Chat.Print($"window not available");
    }
}
