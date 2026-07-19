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
        RegisterFunc("IsContentComplete", (uint cfcId) => IsContentComplete(cfcId));
        RegisterFunc("EntrustAll", () => Svc.Commands.ProcessCommand("/glamourlog store"));
        RegisterFunc("IsBusy", () => Svc.Automation.CurrentTask is not null);
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

    private static bool IsItemInArmoire(uint itemId)
        => Svc.Get<OwnershipService>().Query().Locate(itemId) is PieceLocation.Armoire;

    private static List<uint> GetArmoireItemIds() {
        Svc.Get<OwnershipService>().BuildLalaExport(out _, out var armoires);
        return [.. armoires];
    }

    private static List<uint> GetDresserItemIds() {
        var ownership = Svc.Get<OwnershipService>();
        ownership.BuildLalaExport(out var outfitsBySetId, out _);
        var dresserIds = ownership.GetDresserItemIds();
        var setTokens = Svc.Get<CatalogService>().GlamourSets.Select(s => s.ItemId).ToHashSet();
        var result = new HashSet<uint>(dresserIds.Where(id => !setTokens.Contains(id)));
        foreach (var pieces in outfitsBySetId.Values) {
            foreach (var id in pieces)
                result.Add(id);
        }
        return [.. result.OrderBy(x => x)];
    }

    private static bool IsItemInDresser(uint itemId)
        => Svc.Get<OwnershipService>().IsItemInDresser(itemId);

    private static bool IsSetComplete(uint setItemId)
        => Svc.Get<OwnershipService>().IsSetComplete(setItemId);

    private static bool IsSourceFromDuty(uint cfcId, ItemSource src)
        => src is ItemDungeonChestSource chest && chest.ContentFinderCondition.RowId == cfcId
            || src is ItemDungeonDropSource drop && drop.ContentFinderCondition.RowId == cfcId;

    private static bool IsContentComplete(uint cfcId) {
        if (cfcId == 0 || ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true })
            return false;
        var catalog = Svc.Get<CatalogService>();
        if (!catalog.CatalogReady)
            return false;

        var q = Svc.Get<OwnershipService>().Query();
        var cache = Svc.SheetManager.ItemInfoCache;
        var any = false;
        foreach (var set in catalog.GlamourSets) {
            var status = q.For(set);
            foreach (var pieceId in set.Items) {
                if (pieceId == 0)
                    continue;
                if (cache.GetItemSources(pieceId) is not { Count: > 0 } list)
                    continue;
                if (!list.Any(src => IsSourceFromDuty(cfcId, src)))
                    continue;
                any = true;
                if (status.Piece(pieceId) is not { IsOwned: true })
                    return false;
            }
        }
        return any;
    }

    private static List<uint> GetItemsFromContent(uint cfcId) {
        if (cfcId == 0 || ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true })
            return [];
        var cache = Svc.SheetManager.ItemInfoCache;
        var result = new HashSet<uint>();
        foreach (var row in Item.Where(i => i.RowId > 0)) {
            if (cache.GetItemSources(row.RowId) is not { Count: > 0 } list)
                continue;
            if (list.Any(src => IsSourceFromDuty(cfcId, src)))
                result.Add(row.RowId);
        }
        return [.. result.OrderBy(x => x)];
    }
}
