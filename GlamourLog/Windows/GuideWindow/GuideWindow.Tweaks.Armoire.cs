using GlamourLog.Features.Cabinet;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page TweaksArmoire = new() {
        CategoryTitle = "Tweaks",
        SubCategoryTitle = "Armoire",
        Blocks = [
            new CheckboxSettingBlock(
                "Hide already deposited items",
                "When the armoire window is open, all entries that already exist inside the armoire will be hidden.",
                () => C.HideCabinetOwnedItems,
                v => C.HideCabinetOwnedItems = v,
                () => Svc.Get<CabinetListHandler>().OnConfigChanged()
            ),
            new CheckboxSettingBlock(
                "Hide items in gearsets",
                "When the armoire window is open, all entries that are part of gearsets will be hidden",
                () => C.HideCabinetGearsetItems,
                v => C.HideCabinetGearsetItems = v,
                () => Svc.Get<CabinetListHandler>().OnConfigChanged()
            ),
        ],
    };
}
