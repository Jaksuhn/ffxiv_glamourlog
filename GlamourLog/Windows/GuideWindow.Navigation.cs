namespace GlamourLog;

public partial class GuideWindow {
    // property: page statics live in other partials; field init order is undefined
    private static GuideCategoryNav[] NavCategories
        => [
            new("Guide", [Icons, GuideCounters, Integrations]),
            new("Tweaks", [TweaksComingSoon]),
            new("Settings", [SettingsComingSoon]),
    ];
}
