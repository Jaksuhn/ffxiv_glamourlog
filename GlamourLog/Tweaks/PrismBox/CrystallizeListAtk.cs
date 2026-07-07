using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.PrismBox;

// atk buffer read/write for crystallize tree node 11; layout captured from LoadAtkValues
internal readonly struct CrystallizeAtkSlot {
    internal bool IsLeaf { get; init; }
    internal TreeListItemType ItemType { get; init; }
    internal int SourceIndex { get; init; } // index into agent CrystallizeItems / _categoryRows
}

// per-item uint/string slice offsets inside addon->AtkValues
internal readonly struct CrystallizeAtkBufferLayout {
    internal int UintValuesOffset { get; init; }
    internal int StringValuesOffset { get; init; }
    internal int UintValuesPerItem { get; init; }
    internal int StringValuesPerItem { get; init; }

    internal bool IsValid
        => UintValuesPerItem > 0 && StringValuesPerItem >= 0 && UintValuesOffset >= 0 && StringValuesOffset > UintValuesOffset;

    internal bool Matches(CrystallizeAtkBufferLayout other)
        => UintValuesOffset == other.UintValuesOffset && StringValuesOffset == other.StringValuesOffset && UintValuesPerItem == other.UintValuesPerItem && StringValuesPerItem == other.StringValuesPerItem;
}

internal static unsafe class CrystallizeListAtk {
    internal static AtkValue[] Clone(AtkValue[] source) {
        var copy = new AtkValue[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    internal static int InferItemCount(AtkValue[] atkValues, CrystallizeAtkBufferLayout layout) {
        var slotCount = 0;
        for (var itemIndex = 0; itemIndex < 200; itemIndex++) {
            var baseIndex = layout.UintValuesOffset + itemIndex * layout.UintValuesPerItem;
            if (baseIndex + layout.UintValuesPerItem > atkValues.Length)
                break;

            if (!SlotHasItemData(atkValues, baseIndex, layout.UintValuesPerItem))
                break;

            slotCount = itemIndex + 1;
        }

        return slotCount;
    }

    // caps InferItemCount — stop at first leaf whose source index exceeds categoryRowCount
    internal static int InferBoundedItemCount(AtkValue[] atkValues, int inferredCount, int categoryRowCount, bool includeHeaders, CrystallizeAtkBufferLayout layout) {
        if (inferredCount <= 0 || categoryRowCount <= 0)
            return inferredCount;

        var validCount = 0;
        for (var itemIndex = 0; itemIndex < inferredCount; itemIndex++) {
            var baseIndex = layout.UintValuesOffset + itemIndex * layout.UintValuesPerItem;
            if (baseIndex + layout.UintValuesPerItem > atkValues.Length)
                break;
            if (!SlotHasItemData(atkValues, baseIndex, layout.UintValuesPerItem))
                break;

            var u0 = ReadUInt(atkValues, baseIndex);
            var u1 = ReadUInt(atkValues, baseIndex + 1);
            if (IsTreeItemType(u0)) {
                if (IsLeafType(u0)) {
                    if (!IsValidSourceIndex((int)u1, categoryRowCount))
                        break;
                }
                else if (IsHeaderType(u0)) {
                    if (!includeHeaders)
                        break;
                }
                else {
                    break;
                }
            }
            else if (!IsValidSourceIndex((int)u0, categoryRowCount)) {
                break;
            }

            validCount = itemIndex + 1;
        }

        return validCount > 0 ? validCount : inferredCount;
    }

    internal static void ClearSlots(AtkValue[] atkValues, int firstSlot, int lastSlot, CrystallizeAtkBufferLayout layout) {
        if (firstSlot >= lastSlot)
            return;
        fixed (AtkValue* atkPtr = atkValues)
            ClearItemBlocks(atkPtr, layout, firstSlot, lastSlot);
    }

    private static bool SlotHasItemData(AtkValue[] atkValues, int baseIndex, int uintValuesPerItem) {
        for (var u = 0; u < uintValuesPerItem; u++) {
            if (atkValues[baseIndex + u].Type != AtkValueType.Undefined)
                return true;
        }

        return false;
    }

    // tree slot: u0 = AtkComponentTreeListItemType; flat legacy slot: u0 = source index
    internal static CrystallizeAtkSlot[] Parse(AtkValue[] atkValues, int itemCount, CrystallizeAtkBufferLayout layout, int categoryRowCount = 0) {
        if (itemCount <= 0)
            return [];

        var slots = new CrystallizeAtkSlot[itemCount];
        for (var slot = 0; slot < itemCount; slot++) {
            var baseIndex = layout.UintValuesOffset + slot * layout.UintValuesPerItem;
            var u0 = ReadUInt(atkValues, baseIndex);
            var u1 = ReadUInt(atkValues, baseIndex + 1);

            if (IsTreeItemType(u0)) {
                var itemType = (TreeListItemType)u0;
                var isLeaf = itemType is TreeListItemType.None or TreeListItemType.LastItemInGroup;
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
                ItemType = TreeListItemType.None,
                SourceIndex = flatIsLeaf ? flatIndex : -1,
            };
        }

        return slots;
    }

    // compact hidden slots in working copy; returns new slot count (live addon buffer unchanged until WriteSlotsToAtkBuffer)
    internal static int ApplyToBuffer(
        AtkValue[] atkValues,
        CrystallizeAtkSlot[] layout,
        Func<int, bool> shouldHideLeaf,
        CrystallizeAtkBufferLayout bufferLayout,
        int nativeItemCount,
        bool includeHeaders,
        HashSet<int>? visibleSources = null,
        Func<int, bool>? shouldExcludeSource = null) {

        if (layout.Length == 0)
            return 0;

        var slotLimit = nativeItemCount > 0 ? Math.Min(layout.Length, nativeItemCount) : layout.Length;
        var keepSlots = BuildKeepSlots(layout, shouldHideLeaf, slotLimit, includeHeaders, visibleSources, shouldExcludeSource);
        if (includeHeaders)
            PruneEmptySectionHeaders(keepSlots, layout);
        if (keepSlots.Count == 0)
            return 0;

        var uintValuesOffset = bufferLayout.UintValuesOffset;
        var stringValuesOffset = bufferLayout.StringValuesOffset;
        var uintValuesPerItem = bufferLayout.UintValuesPerItem;
        var stringValuesPerItem = bufferLayout.StringValuesPerItem;

        fixed (AtkValue* atkPtr = atkValues) {
            var scratch = new AtkValue[Math.Max(uintValuesPerItem, stringValuesPerItem)];
            for (var outSlot = 0; outSlot < keepSlots.Count; outSlot++) {
                var layoutSlot = keepSlots[outSlot];
                if (layoutSlot != outSlot) {
                    CopyItemBlocks(atkPtr, uintValuesOffset, stringValuesOffset, uintValuesPerItem, stringValuesPerItem,
                        layoutSlot, outSlot, scratch);
                }
            }

            var leafIndex = 0;
            for (var outSlot = 0; outSlot < keepSlots.Count; outSlot++) {
                var layoutSlot = keepSlots[outSlot];
                ref readonly var entry = ref layout[layoutSlot];
                if (!entry.IsLeaf)
                    continue;

                WriteLeafIndex(atkPtr, uintValuesOffset, uintValuesPerItem, outSlot, leafIndex++);
            }

            var clearThrough = Math.Max(slotLimit, layout.Length);
            ClearItemBlocks(atkPtr, bufferLayout, keepSlots.Count, clearThrough); // zero slots after compacted tail
        }

        return keepSlots.Count;
    }

    private static void ClearItemBlocks(AtkValue* atkValues, CrystallizeAtkBufferLayout layout, int firstClearedSlot, int lastSlot) {
        var empty = default(AtkValue);
        var uintValuesOffset = layout.UintValuesOffset;
        var uintValuesPerItem = layout.UintValuesPerItem;
        var stringValuesOffset = layout.StringValuesOffset;
        var stringValuesPerItem = layout.StringValuesPerItem;
        for (var slot = firstClearedSlot; slot < lastSlot; slot++) {
            for (var u = 0; u < uintValuesPerItem; u++)
                atkValues[uintValuesOffset + slot * uintValuesPerItem + u] = empty;
            if (stringValuesPerItem > 0) {
                for (var s = 0; s < stringValuesPerItem; s++)
                    atkValues[stringValuesOffset + slot * stringValuesPerItem + s] = empty;
            }
        }
    }

    private static bool IsRealHeader(CrystallizeAtkSlot entry)
        => !entry.IsLeaf && entry.ItemType is TreeListItemType.SectionHeader or TreeListItemType.Group;

    private static bool IsHeaderType(uint value)
        => value is (uint)TreeListItemType.SectionHeader or (uint)TreeListItemType.Group;

    private static bool IsLeafType(uint value)
        => value is (uint)TreeListItemType.None or (uint)TreeListItemType.LastItemInGroup;

    internal static void CopySlotToTreeItem(AtkValue[] atkValues, int slot, AtkComponentTreeListItem* item, CrystallizeAtkBufferLayout layout) {
        var uintBase = layout.UintValuesOffset + slot * layout.UintValuesPerItem;
        var uints = item->UIntValues;
        for (var u = 0; u < layout.UintValuesPerItem; u++) {
            var value = ReadUInt(atkValues, uintBase + u);
            if (value == uint.MaxValue)
                continue;

            if ((uint)u < (uint)uints.Count)
                uints[u] = value;
        }

        var strIndex = layout.StringValuesOffset + slot * layout.StringValuesPerItem;
        if ((uint)strIndex >= (uint)atkValues.Length)
            return;

        ref var atkString = ref atkValues[strIndex];
        if (atkString.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString))
            return;

        var strings = item->StringValues;
        if (strings.Count == 0)
            return;

        strings[0] = atkString.String;
    }

    internal static void ClearTreeItemDisplay(AtkComponentTreeListItem* item) {
        if (item is null)
            return;
        var uints = item->UIntValues;
        for (var i = 0; i < uints.Count; i++)
            uints[i] = 0;
        var strings = item->StringValues;
        if (strings.Count > 0)
            strings[0] = default;
    }

    internal static void WriteSlotsToAtkBuffer(AtkValue[] source, AtkValue* destination, int destLength, CrystallizeAtkBufferLayout layout, int firstSlot, int lastSlot) {
        if (firstSlot >= lastSlot || destination is null)
            return;
        var uintValuesPerItem = layout.UintValuesPerItem;
        var stringValuesPerItem = layout.StringValuesPerItem;
        for (var slot = firstSlot; slot < lastSlot; slot++) {
            var uintBase = layout.UintValuesOffset + slot * uintValuesPerItem;
            for (var u = 0; u < uintValuesPerItem; u++) {
                var index = uintBase + u;
                if ((uint)index >= (uint)destLength || (uint)index >= (uint)source.Length)
                    continue;
                destination[index] = source[index];
            }
            if (stringValuesPerItem <= 0)
                continue;
            var strBase = layout.StringValuesOffset + slot * stringValuesPerItem;
            for (var s = 0; s < stringValuesPerItem; s++) {
                var index = strBase + s;
                if ((uint)index >= (uint)destLength || (uint)index >= (uint)source.Length)
                    continue;
                destination[index] = source[index];
            }
        }
    }

    private static void CopyItemBlocks(AtkValue* atkValues, int uintValuesOffset, int stringValuesOffset, int uintValuesPerItem, int stringValuesPerItem, int fromItem, int toItem, AtkValue[] scratch) {
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

    // keep section headers only when includeHeaders and a visible leaf follows
    private static List<int> BuildKeepSlots(CrystallizeAtkSlot[] layout, Func<int, bool> shouldHideLeaf, int slotLimit, bool includeHeaders, HashSet<int>? visibleSources, Func<int, bool>? shouldExcludeSource) {
        if (slotLimit <= 0)
            slotLimit = layout.Length;

        visibleSources ??= [];
        shouldExcludeSource ??= _ => false;

        var keep = new List<int>(layout.Length);
        for (var slot = 0; slot < slotLimit; slot++) {
            ref readonly var entry = ref layout[slot];
            if (IsRealHeader(entry)) {
                if (includeHeaders && SectionHasVisibleLeaf(layout, slot, slotLimit, visibleSources, shouldExcludeSource))
                    keep.Add(slot);
                continue;
            }

            if (!entry.IsLeaf || shouldHideLeaf(entry.SourceIndex))
                continue;

            keep.Add(slot);
        }

        return keep;
    }

    // remove headers whose section has no kept leaves
    private static void PruneEmptySectionHeaders(List<int> keep, CrystallizeAtkSlot[] layout) {
        for (var i = keep.Count - 1; i >= 0; i--) {
            var slot = keep[i];
            if (!IsRealHeader(layout[slot]))
                continue;
            var hasLeaf = false;
            for (var j = i + 1; j < keep.Count; j++) {
                if (IsRealHeader(layout[keep[j]]))
                    break;
                if (layout[keep[j]].IsLeaf) {
                    hasLeaf = true;
                    break;
                }
            }
            if (!hasLeaf)
                keep.RemoveAt(i);
        }
    }

    // header visibility: use visibleSources only (not shouldHideLeaf — duplicate suppression is leaf-only)
    private static bool SectionHasVisibleLeaf(CrystallizeAtkSlot[] layout, int headerIndex, int slotLimit, HashSet<int> visibleSources, Func<int, bool> shouldExcludeSource) {
        for (var i = headerIndex + 1; i < slotLimit; i++) {
            ref readonly var entry = ref layout[i];
            if (IsRealHeader(entry))
                return false;

            if (entry.IsLeaf
                && visibleSources.Contains(entry.SourceIndex)
                && !shouldExcludeSource(entry.SourceIndex))
                return true;
        }

        return false;
    }

    // after compaction rewrite leaf u1 to 0..N-1 display index
    private static void WriteLeafIndex(AtkValue* atkValues, int uintValuesOffset, int uintValuesPerItem, int outSlot, int displayLeafIndex) {
        var baseIndex = uintValuesOffset + outSlot * uintValuesPerItem;
        if (IsTreeItemType(ReadUInt(atkValues, baseIndex))) {
            atkValues[baseIndex + 1].UInt = (uint)displayLeafIndex;
            return;
        }

        // flat row: coerce u0 to Leaf type so u0=2..4 isn't treated as header
        atkValues[baseIndex].Type = AtkValueType.UInt;
        atkValues[baseIndex].UInt = (uint)TreeListItemType.None;
        atkValues[baseIndex + 1].Type = AtkValueType.UInt;
        atkValues[baseIndex + 1].UInt = (uint)displayLeafIndex;
    }

    private static bool IsValidSourceIndex(int sourceIndex, int categoryRowCount)
        => sourceIndex >= 0 && (categoryRowCount <= 0 || sourceIndex < categoryRowCount);

    private static bool IsTreeItemType(uint value)
        => value is <= (uint)TreeListItemType.SectionHeader;

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
