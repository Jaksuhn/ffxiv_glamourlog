using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamourLog.Tweaks.PrismBox;

internal sealed partial class CrystallizeListHandler {
    private unsafe void LogFilterApplied(MiragePrismPrismBoxData* data, int sourceCount, int visibleCount) {
        LogFilterDebug(
            nameof(ApplyFiltersToCrystallizeItems),
            $"category={data->CrystallizeCategory} flags=0x{data->CrystallizeFilterFlags:X2} " +
            $"source={sourceCount} visible={visibleCount} hidden={sourceCount - visibleCount} " +
            $"filters=[{DescribeEnabledFilters()}]");
    }

    protected override string FilterDebugLabel(IRowFilter filter) => filter switch {
        HideDresserDepositedFilter => "owned",
        HideArmoireEligibleFilter => "armoire",
        HideNonOutfitItemsFilter => "non-outfit",
        _ => filter.GetType().Name,
    };
}
