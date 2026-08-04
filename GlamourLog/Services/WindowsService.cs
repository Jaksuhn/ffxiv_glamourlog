using GlamourLog.Windows.GuideWindow;
using KamiToolKit.BaseTypes;
using System.Threading.Tasks;

namespace GlamourLog.Services;

internal sealed class WindowsService : IPluginService, IAsyncDisposable {
    public int InitOrder => 5;

    private FilterWindow? _filterWindow;
    private AddonFilterWindow? _addonFilterWindow;
    private GuideWindow? _mainMenuWindow;
    private LogWindow? _logWindow;

    public WindowsService() {
        Svc.Interface.UiBuilder.OpenMainUi += ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi += ToggleMainMenu;
    }

    internal FilterWindow FilterWindow => _filterWindow ??= new FilterWindow {
        InternalName = "GlamourLogFilter",
        Title = "Set list filters",
        Size = new Vector2(FilterWindow.WindowWidth, FilterWindow.WindowHeight),
        RememberClosePosition = false,
    };

    internal AddonFilterWindow AddonFilterWindow => _addonFilterWindow ??= new AddonFilterWindow {
        InternalName = "GlamourLogAddonFilter",
        Title = "Filters",
        Size = new Vector2(AddonFilterWindow.WindowWidth, AddonFilterWindow.HeightFor(2)),
        RememberClosePosition = false,
    };

    internal GuideWindow MainMenuWindow => _mainMenuWindow ??= new GuideWindow {
        InternalName = "GlamourLogGuide",
        Title = "Help & Settings",
        Size = new Vector2(GuideWindow.WindowWidth, GuideWindow.WindowHeight),
        RememberClosePosition = false,
    };

    internal LogWindow LogWindow => _logWindow ??= new LogWindow(FilterWindow) {
        InternalName = "GlamourLog",
        Title = "Glamour Log",
        Size = new Vector2(920f, 660f),
        RememberClosePosition = false,
    };

    internal void ToggleMainWindow() => Svc.Framework.Run(LogWindow.Toggle);
    internal void ToggleMainMenu() => Svc.Framework.Run(MainMenuWindow.OpenOrToggleCentered);

    internal void ToggleMainMenuNearLogWindow() {
        if (LogWindow.IsOpen)
            MainMenuWindow.OpenOrToggleNear(LogWindow.ComputeMainMenuScreenOrigin());
        else
            MainMenuWindow.OpenOrToggleCentered();
    }

    internal void RefreshLogWindow() => LogWindow.RefreshListsAndDetails();

    public async ValueTask DisposeAsync() {
        Svc.Interface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi -= ToggleMainMenu;

        // all native disposals must be done on game thread
        await Svc.Framework.RunOnFrameworkThread(() => {
            DisposeWindow(_logWindow, nameof(LogWindow));
            DisposeWindow(_filterWindow, nameof(FilterWindow));
            DisposeWindow(_addonFilterWindow, nameof(AddonFilterWindow));
            DisposeWindow(_mainMenuWindow, nameof(GuideWindow));
        });

        _filterWindow = null;
        _addonFilterWindow = null;
        _mainMenuWindow = null;
        _logWindow = null;
    }

    private static void DisposeWindow(NativeAddon? window, string name) {
        if (window is null)
            return;
        try {
            window.Dispose();
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(WindowsService)}] Failed to dispose {name}");
        }
    }
}
