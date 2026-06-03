using GlamourLog.Features.PrismBox;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page TweaksDresser = new() {
        CategoryTitle = "Tweaks",
        SubCategoryTitle = "Dresser",
        Blocks = [
            new CheckboxSettingBlock(
                "Hide already deposited items",
                "When the glamour creation window is open, items already in the glamour dresser (loose or in an outfit) are hidden.",
                () => C.HideCrystallizeOwnedItems,
                v => C.HideCrystallizeOwnedItems = v,
                () => Svc.Get<CrystallizeListHandler>().OnConfigChanged()
            ),
            new CheckboxSettingBlock(
                "Hide armoire-eligible items",
                "When the glamour creation window is open, items that can be stored in the armoire are hidden (whether or not you already own them there).",
                () => C.HideCrystallizeArmoireEligibleItems,
                v => C.HideCrystallizeArmoireEligibleItems = v,
                () => Svc.Get<CrystallizeListHandler>().OnConfigChanged()
            ),
            new CheckboxSettingBlock(
                "Hide non-outfit items",
                "When the glamour creation window is open, items that are not part of any outfit set are hidden.",
                () => C.HideCrystallizeNonOutfitItems,
                v => C.HideCrystallizeNonOutfitItems = v,
                () => Svc.Get<CrystallizeListHandler>().OnConfigChanged()
            ),
        ],
    };
}
