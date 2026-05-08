using Dalamud.IoC;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GlamourLog.Services;

public class Svc {
    [PluginService] public static IChatGui Chat { get; set; } = null!;
    [PluginService] public static IClientState ClientState { get; set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] public static IDataManager Data { get; set; } = null!;
    [PluginService] public static IFramework Framework { get; set; } = null!;
    [PluginService] public static IPluginLog PluginLog { get; set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; set; } = null!;

    public static Configuration Config { get; set; } = null!;
    internal static CatalogService Catalog { get; set; } = null!;
    internal static OwnershipService Ownership { get; set; } = null!;
    internal static WindowsService Windows { get; set; } = null!;
}
