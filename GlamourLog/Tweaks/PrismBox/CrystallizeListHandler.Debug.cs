using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.PrismBox;

internal sealed partial class CrystallizeListHandler {
    private unsafe void LogFilterApplied(MiragePrismPrismBoxData* data, AtkUnitBase* addon, int sourceCount, int visibleCount) {
        LogFilterDebug(
            nameof(TryApplyFilterPipeline),
            $"category={data->CrystallizeCategory} flags=0x{data->CrystallizeFilterFlags:X2} " +
            $"source={sourceCount} visible={visibleCount} hidden={sourceCount - visibleCount} " +
            $"filters=[{DescribeEnabledFilters()}] {DescribeTreeState(addon, data)}");
    }

    private unsafe string DescribeTreeState(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _nativeTree.Resolve(addon);
        var tree = _nativeTree.TreeList;
        var nativeVisible = tree is not null && ((AtkResNode*)tree)->IsVisible();
        return
            $"reported={data->CrystallizeItemCount} nativeVisible={nativeVisible} " +
            $"slots={_nativeTree.NativeSlotCount}->{_nativeTree.FilteredSlotCount} " +
            $"hasBufferLayout={_nativeTree.HasBufferLayout} hasLayout={_nativeTree.HasLayout}";
    }

    protected override string FilterDebugLabel(IRowFilter filter) => filter switch {
        HideDresserDepositedFilter => "owned",
        HideArmoireEligibleFilter => "armoire",
        HideNonOutfitItemsFilter => "non-outfit",
        _ => filter.GetType().Name,
    };
}
