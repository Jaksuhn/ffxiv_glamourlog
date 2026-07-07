using GlamourLog.Windows.GuideWindow;
using KamiToolKit.BaseTypes;
using System.Threading.Tasks;

namespace GlamourLog.Services;

internal sealed class WindowsService : IAsyncDisposable {
    private bool _disposed;
    private FilterWindow? _filterWindow;
    private GuideWindow? _mainMenuWindow;
    private LogWindow? _logWindow;

    internal FilterWindow FilterWindow => _filterWindow ??= new FilterWindow {
        InternalName = "GlamourLogFilter",
        Title = "Set list filters",
        Size = new Vector2(FilterWindow.WindowWidth, FilterWindow.WindowHeight),
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
        if (_disposed)
            return;
        _disposed = true;

        // LogWindow owns the filter-window relationship; tear it down first.
        // NativeAddon: Close/Dispose on main thread; CloseAsync/DisposeAsync off it.
        if (ThreadSafety.IsMainThread) {
            DisposeWindowSync(_logWindow, nameof(LogWindow));
            DisposeWindowSync(_filterWindow, nameof(FilterWindow));
            DisposeWindowSync(_mainMenuWindow, nameof(GuideWindow));
        }
        else {
            await DisposeWindowAsync(_logWindow, nameof(LogWindow));
            await DisposeWindowAsync(_filterWindow, nameof(FilterWindow));
            await DisposeWindowAsync(_mainMenuWindow, nameof(GuideWindow));
        }

        _filterWindow = null;
        _mainMenuWindow = null;
        _logWindow = null;
    }

    private static void DisposeWindowSync(NativeAddon? window, string name) {
        if (window is null)
            return;
        try {
            window.Dispose();
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(WindowsService)}] Failed to dispose {name}");
        }
    }

    private static async ValueTask DisposeWindowAsync(NativeAddon? window, string name) {
        if (window is null)
            return;
        try {
            await window.DisposeAsync();
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"[{nameof(WindowsService)}] Failed to dispose {name}");
        }
    }
}
