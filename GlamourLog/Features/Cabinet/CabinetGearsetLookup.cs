using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace GlamourLog.Features.Cabinet;

internal static unsafe class CabinetGearsetLookup {
    private static readonly HashSet<uint> _itemIds = [];
    private static bool _isDirty = true;

    internal static void Invalidate() => _isDirty = true;

    internal static bool Contains(uint itemId) {
        if (itemId == 0)
            return false;

        if (_isDirty)
            Rebuild();

        return _itemIds.Contains(ItemUtil.GetBaseId(itemId).ItemId);
    }

    private static void Rebuild() {
        _itemIds.Clear();
        var gearsetModule = RaptureGearsetModule.Instance();

        for (var i = 0; i < gearsetModule->NumGearsets; i++) {
            if (!gearsetModule->IsValidGearset((byte)i))
                continue;

            var gearset = gearsetModule->GetGearset(i);
            if (gearset is null || !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            foreach (var gearsetItem in gearset->Items) {
                var baseId = ItemUtil.GetBaseId(gearsetItem.ItemId).ItemId;
                if (baseId != 0)
                    _itemIds.Add(baseId);
            }
        }

        _isDirty = false;
    }
}
