using GlamourLog.Services;

namespace GlamourLog.Features.Cabinet;

internal sealed class HideDepositedItemsFilter : ICabinetRowFilter {
    public bool IsEnabled => C.HideCabinetOwnedItems;
    public bool ShouldHide(CabinetItemRenderer row)
        => row.ItemId != 0 && row.IsStorable && Svc.Get<OwnershipService>().IsItemInArmoire(row.ItemId);
}

internal sealed class HideGearsetItemsFilter : ICabinetRowFilter {
    public bool IsEnabled => C.HideCabinetGearsetItems;
    public bool ShouldHide(CabinetItemRenderer row) => row.ItemId != 0 && CabinetGearsetLookup.Contains(row.ItemId);
}
