namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static CategoryNav[] NavCategories
        => [
            new("Guide", [Icons, GuideCounters, Integrations]),
            new("Tweaks", [TweaksArmoire, TweaksDresser, TweaksChatAlerts]),
            new("Settings", [SettingsLogWindow]),
            #if DEBUG
            new("Debug", [DebugCircleButtons]),
            #endif
    ];
}

internal sealed record CategoryNav(string Title, Page[] Pages);
