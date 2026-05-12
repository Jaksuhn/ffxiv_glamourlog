namespace GlamourLog.Services;

internal sealed class WindowsService : IDisposable {
    private FilterWindow FilterWindow { get; }
    private LogWindow LogWindow { get; }

    public WindowsService() {
        FilterWindow = new FilterWindow {
            InternalName = "GlamourLogFilter",
            Title = "Set list filters",
            Size = new Vector2(FilterWindow.WindowWidth, FilterWindow.WindowHeight),
            RememberClosePosition = false,
        };
        LogWindow = new LogWindow(FilterWindow) {
            InternalName = "GlamourLog",
            Title = "Glamour Sets",
            Size = new Vector2(920f, 640f),
        };
    }

    internal void ToggleMainWindow() => LogWindow.Toggle();
    internal void RefreshLogWindow() => LogWindow.RefreshListsAndDetails();

    public void Dispose() {
        FilterWindow.Dispose();
        LogWindow.Dispose();
    }
}
