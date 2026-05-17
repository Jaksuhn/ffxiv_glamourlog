namespace GlamourLog.Features.Cabinet;

internal interface ICabinetRowFilter {
    bool IsEnabled { get; }
    bool ShouldHide(uint itemId);
}
