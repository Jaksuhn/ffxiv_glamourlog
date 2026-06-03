using GlamourLog.Services;

namespace GlamourLog.Features.PrismBox;

internal interface IPrismBoxRowFilter {
    bool IsEnabled { get; }
    bool ShouldHide(uint itemId);
}

internal sealed class HideDresserDepositedFilter : IPrismBoxRowFilter {
    public bool IsEnabled => C.HideCrystallizeOwnedItems;
    public bool ShouldHide(uint itemId)
        => itemId != 0 && Svc.Get<OwnershipService>().IsItemInGlamourDresser(ItemUtil.GetBaseId(itemId).ItemId);
}

internal sealed class HideArmoireEligibleFilter : IPrismBoxRowFilter {
    public bool IsEnabled => C.HideCrystallizeArmoireEligibleItems;
    public bool ShouldHide(uint itemId)
        => itemId != 0 && Svc.Get<OwnershipService>().IsArmoireEligible(itemId);
}

internal sealed class HideNonOutfitItemsFilter : IPrismBoxRowFilter {
    public bool IsEnabled => C.HideCrystallizeNonOutfitItems;
    public bool ShouldHide(uint itemId) {
        itemId = ItemUtil.GetBaseId(itemId).ItemId;
        if (itemId == 0)
            return false;

        var catalog = Svc.Get<CatalogService>();
        if (!catalog.CatalogReady)
            return false;

        return !catalog.IsMirageOutfitPiece(itemId);
    }
}
