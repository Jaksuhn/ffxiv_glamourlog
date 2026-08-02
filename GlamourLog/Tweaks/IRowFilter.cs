namespace GlamourLog.Tweaks;

internal interface IRowFilter {
    bool IsEnabled { get; }
    bool ShouldHide(uint itemId);
}
