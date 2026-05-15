using AllaganLib.GameSheets.ItemSources;
using GlamourLog.Services;

namespace GlamourLog;

internal sealed class IpcProvider : IDisposable {
    private readonly List<System.Action> _providers = [];

    public IpcProvider() {
        RegisterFunc("GetArmoireItemIds", GetArmoireItemIds);
        RegisterFunc("GetDresserItemIds", GetDresserItemIds);

        RegisterFunc("IsItemOwned", (uint itemId) => IsItemOwned(itemId));
        RegisterFunc("IsItemInArmoire", (uint itemId) => IsItemInArmoire(itemId));
        RegisterFunc("IsItemInDresser", (uint itemId) => IsItemInDresser(itemId));
        RegisterFunc("IsSetComplete", (uint setItemId) => IsSetComplete(setItemId));
        RegisterFunc("GetItemsFromContent", (uint cfcId) => GetItemsFromContent(cfcId));
    }

    public void Dispose() {
        _providers.ForEach(p => p());
        _providers.Clear();
    }

    private void RegisterFunc<TRet>(string name, Func<TRet> func) {
        var p = Svc.Interface.GetIpcProvider<TRet>($"{Svc.Interface.Manifest.InternalName}.{name}");
        p.RegisterFunc(func);
        _providers.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1>(string name, Func<T1, TRet> func) {
        var p = Svc.Interface.GetIpcProvider<T1, TRet>($"{Svc.Interface.Manifest.InternalName}.{name}");
        p.RegisterFunc(func);
        _providers.Add(p.UnregisterFunc);
    }

    private static bool IsItemOwned(uint itemId) => IsItemInArmoire(itemId) || IsItemInDresser(itemId);
    private static bool IsItemInArmoire(uint itemId) => Svc.Get<OwnershipService>().GetItemStorageState(itemId, null) == ItemStorageState.Armoire;

    private static List<uint> GetArmoireItemIds() {
        Svc.Get<OwnershipService>().GetLalaAchievementsExportBuckets(out _, out var armoires);
        return [.. armoires];
    }

    private static List<uint> GetDresserItemIds() {
        var ownership = Svc.Get<OwnershipService>();
        ownership.GetLalaAchievementsExportBuckets(out var outfitsBySetId, out _);
        var dresserIds = ownership.GetDresserStoredItemIds();
        var setTokens = Svc.Get<CatalogService>().GlamourSets.Select(s => s.ItemId).ToHashSet();
        var result = new HashSet<uint>(dresserIds.Where(id => !setTokens.Contains(id)));
        foreach (var pieces in outfitsBySetId.Values) {
            foreach (var id in pieces)
                result.Add(id);
        }

        return [.. result.OrderBy(x => x)];
    }

    private static bool IsItemInDresser(uint itemId) {
        var ownership = Svc.Get<OwnershipService>();
        var snap = ownership.CaptureSnapshot();
        var catalog = Svc.Get<CatalogService>();
        if (snap.DresserItemIds.Contains(itemId) && !catalog.GlamourSets.Select(s => s.ItemId).Contains(itemId))
            return true;
        return catalog.GlamourSets.Any(s => s.Items.Contains(itemId) && ownership.GetItemStorageState(itemId, s, snap) is ItemStorageState.DresserSet);
    }

    private static bool IsSetComplete(uint setItemId) {
        if (Svc.Get<CatalogService>().GlamourSets.FirstOrDefault(s => s.ItemId == setItemId) is not { } set)
            return false;
        return Svc.Get<OwnershipService>().IsSetCompleted(set);
    }

    private static List<uint> GetItemsFromContent(uint cfcId) {
        if (cfcId == 0 || ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true })
            return [];
        var cache = Svc.SheetManager.ItemInfoCache;
        var result = new HashSet<uint>();
        foreach (var row in Item.Where(i => i.RowId > 0)) {
            if (cache.GetItemSources(row.RowId) is not { Count: > 0 } list)
                continue;
            foreach (var src in list) {
                if (src is ItemDungeonChestSource chest && chest.ContentFinderCondition.RowId == cfcId || src is ItemDungeonDropSource drop && drop.ContentFinderCondition.RowId == cfcId) {
                    result.Add(row.RowId);
                    break;
                }
            }
        }

        return [.. result.OrderBy(x => x)];
    }
}
