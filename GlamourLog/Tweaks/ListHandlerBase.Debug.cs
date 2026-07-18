namespace GlamourLog.Features;

internal abstract partial class ListHandlerBase {
    protected void LogFilterDebug(string phase, string message)
        => Svc.Log.Debug($"[{GetType().Name}.{phase}] {message}");

    protected string DescribeEnabledFilters() {
        var enabled = Filters.Where(f => f.IsEnabled).Select(FilterDebugLabel).ToArray();
        return enabled.Length == 0 ? "none" : string.Join(", ", enabled);
    }

    protected abstract string FilterDebugLabel(IRowFilter filter);
}
