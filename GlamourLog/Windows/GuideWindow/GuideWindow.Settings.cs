using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;

namespace GlamourLog.Windows.GuideWindow;

public unsafe partial class GuideWindow {
    private static readonly Page SettingsLogWindow = new() {
        CategoryTitle = "Settings",
        SubCategoryTitle = "Glamour Log Window",
        Blocks = [
            new CheckboxSettingBlock(
                "Disable force window closing",
                "Prevents the game from closing the addon when you go through an area transition or cutscene. Will still hide the addon. Also disables the ability to close via ESC. Must be clicked manually.",
                () => C.DisableClose,
                v => C.DisableClose = v,
                () => {
                    AtkUnitBase* addon = Svc.Get<WindowsService>().LogWindow;
                    if (addon is not null)
                        addon->ShouldFireCallbackAndHideOrClose = C.DisableClose;
                }),
        ],
    };
}
