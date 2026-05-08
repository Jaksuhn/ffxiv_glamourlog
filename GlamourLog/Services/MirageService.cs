using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GlamourLog.Services;

/// <summary> Cache for glamour dresser prism box rows and per-outfit slot unlock flags (<see cref="MirageManager.IsSetSlotUnlocked"/>). </summary>
internal sealed unsafe class MirageService : IDisposable {
    /// <param name="PrismBoxIndex">Index into <see cref="MirageManager"/> prism box item ids (matches <c>IsSetSlotUnlocked</c>).</param>
    /// <param name="ItemId">Equipment piece item id for this slot.</param>
    /// <param name="InOutfit">Whether this slot is registered as part of the partial/full outfit at <paramref name="PrismBoxIndex"/>.</param>
    internal readonly record struct SlotState(int PrismBoxIndex, uint ItemId, bool InOutfit);

    private readonly uint[] _prismBoxItemIds = new uint[800];
    private Dictionary<uint, IReadOnlyList<SlotState>> _slotStatesByOutfitId = [];
    private int _cachedCatalogVersion = -1;

    internal MirageService() {
        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "MiragePrismPrismBox", OnPrismBoxRefresh);

        if (Svc.ClientState.IsLoggedIn)
            OnLogin();
    }

    internal event System.Action? MirageDataChanged;

    public void Dispose() {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "MiragePrismPrismBox", OnPrismBoxRefresh);
        Svc.ClientState.Login -= OnLogin;
        Svc.ClientState.Logout -= OnLogout;
        ClearCache();
    }

    private void OnLogin() {
        Svc.PluginLog.Debug($"[{nameof(MirageService)}] Refreshing prism box.");
        GameMain.ExecuteCommand(2350);
        RefreshCache();
    }

    private void OnLogout(int _, int __) => ClearCache();

    private void OnPrismBoxRefresh(AddonEvent _, AddonArgs __) => BuildCache(notify: true);

    internal void RefreshCache() => BuildCache(notify: true);

    private void ClearCache() {
        var hadAny = _slotStatesByOutfitId.Count > 0;
        _slotStatesByOutfitId = [];
        _cachedCatalogVersion = -1;
        if (hadAny)
            MirageDataChanged?.Invoke();
    }

    private void BuildCache(bool notify) {
        if (!Svc.ClientState.IsLoggedIn) {
            ClearCache();
            return;
        }

        var catalogVersion = Svc.Catalog.DataVersion;
        if (!notify && _slotStatesByOutfitId.Count > 0 && _cachedCatalogVersion == catalogVersion)
            return;

        var mm = MirageManager.Instance();
        if (mm is null || !mm->PrismBoxLoaded)
            return;

        mm->PrismBoxItemIds.CopyTo(_prismBoxItemIds);
        var next = RebuildSlotMap(mm);
        var changed = !MapsEqual(_slotStatesByOutfitId, next);
        _slotStatesByOutfitId = next;
        _cachedCatalogVersion = catalogVersion;

        if (notify && changed)
            MirageDataChanged?.Invoke();
    }

    private static bool MapsEqual(Dictionary<uint, IReadOnlyList<SlotState>> lhs, Dictionary<uint, IReadOnlyList<SlotState>> rhs) {
        if (lhs.Count != rhs.Count)
            return false;

        foreach (var (setId, leftSlots) in lhs) {
            if (!rhs.TryGetValue(setId, out var rightSlots))
                return false;
            if (leftSlots.Count != rightSlots.Count)
                return false;
            for (var i = 0; i < leftSlots.Count; i++) {
                if (leftSlots[i] != rightSlots[i])
                    return false;
            }
        }

        return true;
    }

    private Dictionary<uint, IReadOnlyList<SlotState>> RebuildSlotMap(MirageManager* mm) {
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

        return map;
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
        BuildCache(notify: false);
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
