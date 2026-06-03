using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.PrismBox;

internal readonly struct CrystallizeAtkSlot {
    internal bool IsLeaf { get; init; }
    internal AtkComponentTreeListItemType ItemType { get; init; }
    internal int SourceIndex { get; init; }
}

internal static unsafe class CrystallizeListAtk {
    internal const int UintValuesOffset = 2;
    internal const int StringValuesOffset = 1682;
    internal const int UintValuesPerItem = 6;
    internal const int StringValuesPerItem = 1;

    internal static AtkValue[] Clone(AtkValue[] source) {
        var copy = new AtkValue[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    internal static int InferItemCount(AtkValue[] atkValues) {
        var maxIndex = -1;
        for (var itemIndex = 0; itemIndex < 200; itemIndex++) {
            var baseIndex = UintValuesOffset + itemIndex * UintValuesPerItem;
            if (baseIndex >= atkValues.Length)
                break;

            for (var u = 0; u < UintValuesPerItem; u++) {
                ref var value = ref atkValues[baseIndex + u];
                if (value.Type != AtkValueType.UInt)
                    continue;

                if (value.UInt < 200)
                    maxIndex = Math.Max(maxIndex, (int)value.UInt);
            }
        }

        return maxIndex >= 0 ? maxIndex + 1 : 0;
    }

    internal static CrystallizeAtkSlot[] Parse(AtkValue[] atkValues, int itemCount, int categoryRowCount = 0) {
        if (itemCount <= 0)
            return [];

        var slots = new CrystallizeAtkSlot[itemCount];
        for (var slot = 0; slot < itemCount; slot++) {
            var baseIndex = UintValuesOffset + slot * UintValuesPerItem;
            var u0 = ReadUInt(atkValues, baseIndex);
            var u1 = ReadUInt(atkValues, baseIndex + 1);

            if (IsTreeItemType(u0)) {
                var itemType = (AtkComponentTreeListItemType)u0;
                var isLeaf = itemType is AtkComponentTreeListItemType.Leaf or AtkComponentTreeListItemType.LastLeafInGroup;
                var sourceIndex = isLeaf ? (int)u1 : -1;
                if (isLeaf && !IsValidSourceIndex(sourceIndex, categoryRowCount))
                    isLeaf = false;

                slots[slot] = new CrystallizeAtkSlot {
                    IsLeaf = isLeaf,
                    ItemType = itemType,
                    SourceIndex = isLeaf ? sourceIndex : -1,
                };
                continue;
            }

            var flatIndex = (int)u0;
            var flatIsLeaf = IsValidSourceIndex(flatIndex, categoryRowCount);
            slots[slot] = new CrystallizeAtkSlot {
                IsLeaf = flatIsLeaf,
                ItemType = AtkComponentTreeListItemType.Leaf,
                SourceIndex = flatIsLeaf ? flatIndex : -1,
            };
        }

        return slots;
    }

    internal static int CountVisibleSlots(CrystallizeAtkSlot[] layout, Func<int, bool> shouldHideSource, bool includeHeaders)
        => BuildKeepSlots(layout, shouldHideSource, layout.Length, includeHeaders).Count;

    internal static int ApplyToBuffer(AtkValue[] atkValues, CrystallizeAtkSlot[] layout, Func<int, bool> shouldHideSource, int uintValuesOffset, int stringValuesOffset, int uintValuesPerItem, int stringValuesPerItem, int nativeItemCount, bool includeHeaders, int[]? atkSlotRemap) {
        if (layout.Length == 0)
            return 0;

        var slotLimit = nativeItemCount > 0 ? Math.Min(layout.Length, nativeItemCount) : layout.Length;
        var keepSlots = BuildKeepSlots(layout, shouldHideSource, slotLimit, includeHeaders);
        if (keepSlots.Count == 0)
            return 0;

        fixed (AtkValue* atkPtr = atkValues) {
            var scratch = new AtkValue[Math.Max(uintValuesPerItem, stringValuesPerItem)];
            for (var outSlot = 0; outSlot < keepSlots.Count; outSlot++) {
                var layoutSlot = keepSlots[outSlot];
                var atkSrcSlot = ResolveRemap(layoutSlot, atkSlotRemap);
                if (atkSrcSlot == outSlot)
                    continue;

                CopyItemBlocks(atkPtr, uintValuesOffset, stringValuesOffset, uintValuesPerItem, stringValuesPerItem,
                    atkSrcSlot, outSlot, scratch);
            }

            var leafIndex = 0;
            for (var outSlot = 0; outSlot < keepSlots.Count; outSlot++) {
                var srcSlot = keepSlots[outSlot];
                if (!layout[srcSlot].IsLeaf)
                    continue;

                WriteLeafIndex(atkPtr, uintValuesOffset, uintValuesPerItem, outSlot, layout[srcSlot], leafIndex++);
            }
        }

        return keepSlots.Count;
    }

    internal static bool IsRealHeader(CrystallizeAtkSlot entry)
        => !entry.IsLeaf && entry.ItemType is AtkComponentTreeListItemType.GroupHeader
            or AtkComponentTreeListItemType.CollapsibleGroupHeader;

    internal static void CopyItemBlocks(AtkValue* atkValues, int uintValuesOffset, int stringValuesOffset, int uintValuesPerItem, int stringValuesPerItem, int fromItem, int toItem, AtkValue[] scratch) {
        for (var i = 0; i < uintValuesPerItem; i++)
            scratch[i] = atkValues[uintValuesOffset + fromItem * uintValuesPerItem + i];
        for (var i = 0; i < uintValuesPerItem; i++)
            atkValues[uintValuesOffset + toItem * uintValuesPerItem + i] = scratch[i];

        if (stringValuesPerItem <= 0)
            return;

        for (var i = 0; i < stringValuesPerItem; i++)
            scratch[i] = atkValues[stringValuesOffset + fromItem * stringValuesPerItem + i];
        for (var i = 0; i < stringValuesPerItem; i++)
            atkValues[stringValuesOffset + toItem * stringValuesPerItem + i] = scratch[i];
    }

    private static List<int> BuildKeepSlots(CrystallizeAtkSlot[] layout, Func<int, bool> shouldHideSource, int slotLimit, bool includeHeaders) {
        if (slotLimit <= 0)
            slotLimit = layout.Length;

        var keep = new List<int>(layout.Length);
        for (var slot = 0; slot < slotLimit; slot++) {
            ref readonly var entry = ref layout[slot];
            if (IsRealHeader(entry)) {
                if (includeHeaders)
                    keep.Add(slot);
                continue;
            }

            if (!entry.IsLeaf || shouldHideSource(entry.SourceIndex))
                continue;

            keep.Add(slot);
        }

        return includeHeaders ? PruneEmptyHeaders(keep, layout) : keep;
    }

    private static List<int> PruneEmptyHeaders(List<int> slots, CrystallizeAtkSlot[] layout) {
        if (slots.Count == 0)
            return slots;

        var pruned = new List<int>(slots.Count);
        for (var i = 0; i < slots.Count; i++) {
            var slot = slots[i];
            if (!IsRealHeader(layout[slot])) {
                pruned.Add(slot);
                continue;
            }

            for (var j = i + 1; j < slots.Count; j++) {
                if (IsRealHeader(layout[slots[j]]))
                    break;

                if (layout[slots[j]].IsLeaf) {
                    pruned.Add(slot);
                    break;
                }
            }
        }

        return pruned;
    }

    private static void WriteLeafIndex(AtkValue* atkValues, int uintValuesOffset, int uintValuesPerItem, int outSlot, CrystallizeAtkSlot template, int displayLeafIndex) {
        var baseIndex = uintValuesOffset + outSlot * uintValuesPerItem;
        if (IsTreeItemType(ReadUInt(atkValues, baseIndex)))
            atkValues[baseIndex + 1].UInt = (uint)displayLeafIndex;
        else
            atkValues[baseIndex].UInt = (uint)displayLeafIndex;
    }

    private static int ResolveRemap(int layoutSlot, int[]? remap)
        => remap is { Length: > 0 } r && (uint)layoutSlot < (uint)r.Length ? r[layoutSlot] : layoutSlot;

    private static bool IsValidSourceIndex(int sourceIndex, int categoryRowCount)
        => sourceIndex >= 0 && (categoryRowCount <= 0 || sourceIndex < categoryRowCount);

    private static bool IsTreeItemType(uint value)
        => value is <= (uint)AtkComponentTreeListItemType.GroupHeader;

    private static uint ReadUInt(AtkValue[] atkValues, int index) {
        if ((uint)index >= (uint)atkValues.Length)
            return uint.MaxValue;

        ref var value = ref atkValues[index];
        return value.Type == AtkValueType.UInt ? value.UInt : uint.MaxValue;
    }

    private static uint ReadUInt(AtkValue* atkValues, int index) {
        ref var value = ref atkValues[index];
        return value.Type == AtkValueType.UInt ? value.UInt : uint.MaxValue;
    }
}
