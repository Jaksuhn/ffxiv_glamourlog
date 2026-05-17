namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    // property: page statics live in other partials; field init order is undefined
    private static CategoryNav[] NavCategories
        => [
            new("Guide", [Icons, GuideCounters, Integrations]),
            new("Tweaks", [TweaksArmoire]),
            new("Settings", [SettingsLogWindow]),
    ];
}

internal sealed record CategoryNav(string Title, Page[] Pages);
