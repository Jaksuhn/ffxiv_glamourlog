using GlamourLog.Windows.GuideWindow;

namespace GlamourLog.Services;

internal sealed class WindowsService : IDisposable {
    private FilterWindow FilterWindow { get; }
    private GuideWindow MainMenuWindow { get; }
    private LogWindow LogWindow { get; }

    public WindowsService() {
        FilterWindow = new FilterWindow {
            InternalName = "GlamourLogFilter",
            Title = "Set list filters",
            Size = new Vector2(FilterWindow.WindowWidth, FilterWindow.WindowHeight),
            RememberClosePosition = false,
        };
        MainMenuWindow = new GuideWindow {
            InternalName = "GlamourLogGuide",
            Title = "Help & Settings",
            Size = new Vector2(GuideWindow.WindowWidth, GuideWindow.WindowHeight),
            RememberClosePosition = false,
        };
        LogWindow = new LogWindow(FilterWindow) {
            InternalName = "GlamourLog",
            Title = "Glamour Log",
            Size = new Vector2(920f, 660f),
        };
    }

    internal void ToggleMainWindow() => LogWindow.Toggle();

    internal void ToggleMainMenu() => MainMenuWindow.OpenOrToggleCentered();

    internal void ToggleMainMenuNearLogWindow() {
        if (LogWindow.IsOpen)
            MainMenuWindow.OpenOrToggleNear(LogWindow.ComputeMainMenuScreenOrigin());
        else
            MainMenuWindow.OpenOrToggleCentered();
    }

    internal void RefreshLogWindow() => LogWindow.RefreshListsAndDetails();

    public void Dispose() {
        FilterWindow.Dispose();
        MainMenuWindow.Dispose();
        LogWindow.Dispose();
    }
}
