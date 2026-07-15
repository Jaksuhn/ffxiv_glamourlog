using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.PrismBox;

internal sealed partial class CrystallizeListHandler {
    private string? _lastFilterSummary;
    private string? _lastApplySummary;
    private string? _lastFilterOnState;
    private string? _lastFilterOffState;
    private string? _lastSnapshotUnavailableSignature;
    private string? _lastCategoryCaptureSignature;

    private void ClearFilterLogSignatures() {
        _lastFilterSummary = null;
        _lastApplySummary = null;
        _lastFilterOnState = null;
        _lastFilterOffState = null;
        _lastSnapshotUnavailableSignature = null;
        _lastCategoryCaptureSignature = null;
    }

    private static void LogFilterDebug(string phase, string message)
        => Svc.Log.Debug($"[{nameof(CrystallizeListHandler)}.{phase}] {message}");

    private static void LogFilterWarning(string message)
        => Svc.Log.Warning($"[{nameof(CrystallizeListHandler)}] {message}");

    private unsafe void LogCategoryCaptureOnce(MiragePrismPrismBoxData* data, int rowCount) {
        var signature = $"cat={data->CrystallizeCategory} rows={rowCount} flags=0x{data->CrystallizeFilterFlags:X2}";
        if (signature == _lastCategoryCaptureSignature)
            return;
        _lastCategoryCaptureSignature = signature;
        LogFilterDebug(nameof(TryCaptureCategorySnapshot), $"captured {rowCount} rows for category {data->CrystallizeCategory} (flags=0x{data->CrystallizeFilterFlags:X2})");
    }

    private unsafe void LogSnapshotUnavailableOnce(MiragePrismPrismBoxData* data) {
        var reported = (int)data->CrystallizeItemCount;
        var scanned = ScanPopulatedCategoryItemCount(data);
        var signature =
            $"cat={data->CrystallizeCategory} reported={reported} scanned={scanned} " +
            $"populating={data->IsPopulatingList} complete={data->IsPopulatingComplete} snapshot={_categoryRows.Length} pass={_snapshotCapturePasses}";
        if (signature == _lastSnapshotUnavailableSignature)
            return;
        _lastSnapshotUnavailableSignature = signature;
        LogFilterDebug(nameof(OnPostRefresh), $"category snapshot unavailable after native refresh ({signature})");
    }

    private void LogFilterRebuildSummary() {
        var hiddenCount = _categoryRows.Length - _displayToSource.Length;
        var summary =
            $"category={_trackedCategory} snapshot={_categoryRows.Length} visible={_displayToSource.Length} hidden={hiddenCount} " +
            $"filters=[{DescribeEnabledFilters()}]";
        if (summary == _lastFilterSummary)
            return;
        _lastFilterSummary = summary;
        LogFilterDebug(nameof(RebuildFilterMap), summary);
    }

    private unsafe void LogFilterApplySummary(MiragePrismPrismBoxData* data, int sourceCount, int visibleCount) {
        var summary =
            $"category={data->CrystallizeCategory} flags=0x{data->CrystallizeFilterFlags:X2} " +
            $"rows={sourceCount} visible={visibleCount}";
        if (summary == _lastApplySummary)
            return;
        _lastApplySummary = summary;
        LogFilterDebug(nameof(TryApplyFilterPipeline), summary);
    }

    private unsafe void LogFilterOnState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        var summary =
            $"visible={_displayToSource.Length} snapshot={_categoryRows.Length} category={_trackedCategory} " +
            $"{DescribeTreeState(addon, data)} nativeSlotCount={_nativeTree.NativeSlotCount} filteredSlotCount={_nativeTree.FilteredSlotCount}";
        if (summary == _lastFilterOnState)
            return;
        _lastFilterOnState = summary;
        LogFilterDebug(phase, summary);
    }

    private unsafe void LogFilterOffState(string phase, AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        var summary =
            $"snapshot={_categoryRows.Length} category={_trackedCategory} {DescribeTreeState(addon, data)}";
        if (summary == _lastFilterOffState)
            return;
        _lastFilterOffState = summary;
        LogFilterDebug(phase, summary);
    }

    private unsafe string DescribeTreeState(AtkUnitBase* addon, MiragePrismPrismBoxData* data) {
        _nativeTree.Resolve(addon);
        var tree = _nativeTree.TreeList;
        var nativeVisible = tree is not null && ((AtkResNode*)tree)->IsVisible();
        return
            $"reported={data->CrystallizeItemCount} nativeVisible={nativeVisible} " +
            $"hasBufferLayout={_nativeTree.HasBufferLayout} hasLayout={_nativeTree.HasLayout}";
    }

    private string DescribeEnabledFilters() {
        var enabled = _filters.Where(f => f.IsEnabled).Select(FilterDebugLabel).ToArray();
        return enabled.Length == 0 ? "none" : string.Join(", ", enabled);
    }

    private static string FilterDebugLabel(IPrismBoxRowFilter filter) => filter switch {
        HideDresserDepositedFilter => "owned",
        HideArmoireEligibleFilter => "armoire",
        HideNonOutfitItemsFilter => "non-outfit",
        _ => filter.GetType().Name,
    };
}
