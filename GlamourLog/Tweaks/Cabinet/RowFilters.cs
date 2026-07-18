using GlamourLog.Services;

namespace GlamourLog.Features.Cabinet;

internal sealed class HideDepositedItemsFilter : IRowFilter {
    public bool IsEnabled => C.HideCabinetOwnedItems;
    public bool ShouldHide(uint itemId)
        => itemId != 0 && Svc.Get<OwnershipService>().IsItemInArmoire(itemId);
}

internal sealed class HideGearsetItemsFilter : IRowFilter {
    public bool IsEnabled => C.HideCabinetGearsetItems;
    public bool ShouldHide(uint itemId) => itemId != 0 && CabinetGearsetLookup.Contains(itemId);
}
