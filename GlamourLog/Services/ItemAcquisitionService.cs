using AllaganLib.GameSheets.Caches;
using AllaganLib.GameSheets.ItemSources;

namespace GlamourLog.Services;

internal sealed class ItemAcquisitionService : IPluginService {
    public int InitOrder => -15; // before UnobtainableService

    internal IReadOnlyList<ItemSource> GetSources(uint itemId)
        => Svc.SheetManager.ItemInfoCache.GetItemSources(itemId) is { Count: > 0 } list ? list : [];

    internal bool HasDutySource(uint itemId)
        => GetSources(itemId).Any(IsDutySource);

    internal bool HasCraftSource(uint itemId)
        => GetSources(itemId).Any(IsCraftSource);

    internal bool HasDutyOrCraftSource(uint itemId)
        => GetSources(itemId).Any(static s => IsDutySource(s) || IsCraftSource(s));

    internal bool HasQuestSource(uint itemId)
        => GetSources(itemId).Any(IsQuestSource);

    // duty chests / boss loot (inc treasure maps)
    internal static bool IsDutySource(ItemSource src)
        => src is ItemDungeonChestSource or ItemDungeonDropSource or ItemDungeonBossChestSource or ItemDungeonBossDropSource;

    internal static bool IsSourceFromCfc(ItemSource src, uint cfcId)
        => src is ItemDungeonChestSource chest && chest.ContentFinderCondition.RowId == cfcId
            || src is ItemDungeonDropSource drop && drop.ContentFinderCondition.RowId == cfcId;

    internal static bool IsCraftSource(ItemSource src)
        => src is ItemCraftResultSource;

    internal static bool IsQuestSource(ItemSource src)
        => src is ItemQuestSource;

    // attire coffers are Loot or Coffer in LuminaSupplemental
    internal static bool IsAttireCofferSource(ItemSource src)
        => TryGetAttireCofferItemId(src) is not null;

    internal static uint? TryGetAttireCofferItemId(ItemSource src)
        => src is ItemSupplementSource { Type: ItemInfoType.Loot or ItemInfoType.Coffer, CostItem.RowId: var id and not 0 } ? id : null;
}
