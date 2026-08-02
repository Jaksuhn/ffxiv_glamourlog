using clib;
using Dalamud.Plugin;
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
    public async Task LoadAsync(CancellationToken cancellationToken) {
#if LOCAL_CS
        dalamud.InitCustomClientStructs();
#endif
        CLibMain.Init(dalamud, this, CLibModule.All);
        KamiToolKitLibrary.Initialize(dalamud);
    }

    public async ValueTask DisposeAsync() {
        await CLibMain.DisposeAsync();
        await Svc.Framework.RunOnFrameworkThread(KamiToolKitLibrary.Dispose);
    }
}
