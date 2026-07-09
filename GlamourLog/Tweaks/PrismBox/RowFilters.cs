using GlamourLog.Services;

namespace GlamourLog.Features.PrismBox;

internal interface IPrismBoxRowFilter {
    bool IsEnabled { get; }
    bool ShouldHide(uint itemId);
}

internal sealed class HideDresserDepositedFilter : IPrismBoxRowFilter {
    public bool IsEnabled => C.HideCrystallizeOwnedItems;
    public bool ShouldHide(uint itemId)
        => itemId != 0 && Svc.Get<OwnershipService>().IsCrystallizeItemFullyDeposited(ItemUtil.GetBaseId(itemId).ItemId);
}

internal sealed class HideArmoireEligibleFilter : IPrismBoxRowFilter {
    public bool IsEnabled => C.HideCrystallizeArmoireEligibleItems;
    public bool ShouldHide(uint itemId)
        => itemId != 0 && Svc.Get<OwnershipService>().IsArmoireCabinetItem(itemId);
}

internal sealed class HideNonOutfitItemsFilter : IPrismBoxRowFilter {
    public bool IsEnabled => C.HideCrystallizeNonOutfitItems;
    public bool ShouldHide(uint itemId) => !MirageStoreSetItemLookup.TryGetRow(itemId, out _);
}

internal static class PrismBoxFilters {
    internal static IPrismBoxRowFilter[] Create() => [
        new HideDresserDepositedFilter(),
        new HideArmoireEligibleFilter(),
        new HideNonOutfitItemsFilter(),
    ];
}
