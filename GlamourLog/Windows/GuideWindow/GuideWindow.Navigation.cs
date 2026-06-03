namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static CategoryNav[] NavCategories
        => [
            new("Guide", [Icons, GuideCounters, Integrations]),
            new("Tweaks", [TweaksArmoire, TweaksDresser]),
            new("Settings", [SettingsLogWindow]),
    ];
}

internal sealed record CategoryNav(string Title, Page[] Pages);
