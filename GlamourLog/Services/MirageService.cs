using FFXIVClientStructs.FFXIV.Client.Game;

namespace GlamourLog.Services;

/// <summary> Cache for glamour dresser prism box rows and per-outfit slot unlock flags (<see cref="MirageManager.IsSetSlotUnlocked"/>). </summary>
internal sealed unsafe class MirageService : IDisposable {
    /// <param name="PrismBoxIndex">Index into <see cref="MirageManager"/> prism box item ids (matches <c>IsSetSlotUnlocked</c>).</param>
    /// <param name="ItemId">Equipment piece item id for this slot.</param>
    /// <param name="InOutfit">Whether this slot is registered as part of the partial/full outfit at <paramref name="PrismBoxIndex"/>.</param>
    internal readonly record struct SlotState(int PrismBoxIndex, uint ItemId, bool InOutfit);

    private bool _hasValidSnapshot;
    private int _snapshotCatalogDataVersion = int.MinValue;
    private readonly uint[] _prismBoxItemIds = new uint[800];
    private Dictionary<uint, IReadOnlyList<SlotState>> _slotStatesByOutfitId = [];

    internal MirageService() {
        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;
        if (Svc.ClientState.IsLoggedIn)
            RequestPrismBoxOnFramework();
    }

    internal event System.Action? MirageDataChanged;

    public void Dispose() {
        Svc.ClientState.Login -= OnLogin;
        Svc.ClientState.Logout -= OnLogout;
    }

    private void OnLogin() => RequestPrismBoxOnFramework();

    private void OnLogout(int _, int __) => InvalidateCache();

    private void RequestPrismBoxOnFramework() {
        var task = Svc.Framework.RunOnFrameworkThread(() => {
            var mm = MirageManager.Instance();
            if (mm is not null && !mm->PrismBoxRequested)
                GameMain.ExecuteCommand(2350);
        });
        task.GetAwaiter().GetResult();
    }

    private void InvalidateCache() {
        _slotStatesByOutfitId = [];
        _hasValidSnapshot = false;
        _snapshotCatalogDataVersion = int.MinValue;
    }

    /// <summary> Rebuilds the slot map when <see cref="MirageManager.PrismBoxLoaded"/> is true. </summary>
    internal void EnsureCacheCurrent() {
        if (!Svc.ClientState.IsLoggedIn)
            return;

        var task = Svc.Framework.RunOnFrameworkThread(EnsureCacheCurrentCore);
        task.GetAwaiter().GetResult();
    }

    private void EnsureCacheCurrentCore() {
        var mm = MirageManager.Instance();
        if (mm is null)
            return;

        var catalogVersion = Svc.Catalog.DataVersion;
        if (mm->PrismBoxLoaded && _hasValidSnapshot && _snapshotCatalogDataVersion == catalogVersion)
            return;

        if (!mm->PrismBoxLoaded)
            return;

        mm->PrismBoxItemIds.CopyTo(_prismBoxItemIds);
        RebuildSlotMap(mm);
        _snapshotCatalogDataVersion = catalogVersion;
        _hasValidSnapshot = true;
        MirageDataChanged?.Invoke();
    }

    private void RebuildSlotMap(MirageManager* mm) {
        var map = new Dictionary<uint, IReadOnlyList<SlotState>>();
        foreach (var set in Svc.Catalog.GlamourSets) {
            var prismIndex = FindPrismIndexForOutfit(set.ItemId);
            if (prismIndex < 0)
                continue;

            var second = FindSecondPrismIndexForOutfit(set.ItemId, prismIndex);
            if (second >= 0)
                Svc.PluginLog.Verbose($"[{nameof(MirageService)}] Multiple prism box entries for outfit item {set.ItemId}: indices {prismIndex}, {second} (using {prismIndex}).");

            // IMPORTANT: IsSetSlotUnlocked expects the original MirageStoreSetItem slot column index,
            // not the compacted index from filtered set.Items.
            var sourceRow = MirageStoreSetItem.GetRow(set.ItemId);
            var sourceSlots = sourceRow.Items
                .Select((item, slotIndex) => (slotIndex, itemId: item.RowId))
                .Where(x => x.itemId > 0)
                .ToList();

            var list = new SlotState[sourceSlots.Count];
            for (var i = 0; i < sourceSlots.Count; i++) {
                var sourceSlot = sourceSlots[i];
                var inOutfit = mm->IsSetSlotUnlocked((uint)prismIndex, sourceSlot.slotIndex);
                list[i] = new SlotState(prismIndex, sourceSlot.itemId, inOutfit);
            }

            map[set.ItemId] = list;
        }

        _slotStatesByOutfitId = map;
    }

    /// <returns> First matching index, or -1. </returns>
    private int FindPrismIndexForOutfit(uint outfitItemId) {
        for (var i = 0; i < _prismBoxItemIds.Length; i++) {
            if (_prismBoxItemIds[i] == outfitItemId)
                return i;
        }

        return -1;
    }

    /// <returns> Second matching index if any, else -1. </returns>
    private int FindSecondPrismIndexForOutfit(uint outfitItemId, int afterIndex) {
        for (var i = afterIndex + 1; i < _prismBoxItemIds.Length; i++) {
            if (_prismBoxItemIds[i] == outfitItemId)
                return i;
        }

        return -1;
    }

    /// <summary> Returns slot states when prism box has been loaded and the outfit token appears in the prism list. </summary>
    internal bool TryGetSlotStates(uint outfitItemId, out IReadOnlyList<SlotState>? slots) {
        EnsureCacheCurrent();
        return _slotStatesByOutfitId.TryGetValue(outfitItemId, out slots);
    }

    /// <summary> True when every defined slot for this set is registered in the mirage outfit at the dresser. </summary>
    internal bool IsFullMirageOutfit(GlamourSet set) {
        if (!TryGetSlotStates(set.ItemId, out var slots) || slots is null)
            return false;
        if (slots.Count != set.Items.Count)
            return false;
        foreach (var s in slots) {
            if (!s.InOutfit)
                return false;
        }

        return true;
    }
}
