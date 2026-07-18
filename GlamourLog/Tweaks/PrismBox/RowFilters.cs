using GlamourLog.Services;

namespace GlamourLog.Features.PrismBox;

internal sealed class HideDresserDepositedFilter : IRowFilter {
    public bool IsEnabled => C.HideCrystallizeOwnedItems;
    public bool ShouldHide(uint itemId)
        => itemId != 0 && Svc.Get<OwnershipService>().IsCrystallizeItemFullyDeposited(ItemUtil.GetBaseId(itemId).ItemId);
}

internal sealed class HideArmoireEligibleFilter : IRowFilter {
    public bool IsEnabled => C.HideCrystallizeArmoireEligibleItems;
    public bool ShouldHide(uint itemId)
        => itemId != 0 && Svc.Get<OwnershipService>().IsCabinetItem(itemId);
}

internal sealed class HideNonOutfitItemsFilter : IRowFilter {
    public bool IsEnabled => C.HideCrystallizeNonOutfitItems;
    public bool ShouldHide(uint itemId) => !MirageStoreSetItemLookup.TryGetRow(itemId, out _);
}

internal static class PrismBoxFilters {
    internal static IRowFilter[] Create() => [
        new HideDresserDepositedFilter(),
        new HideArmoireEligibleFilter(),
        new HideNonOutfitItemsFilter(),
    ];
}
