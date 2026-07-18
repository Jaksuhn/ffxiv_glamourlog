namespace GlamourLog.Features;

internal interface IRowFilter {
    bool IsEnabled { get; }
    bool ShouldHide(uint itemId);
}
