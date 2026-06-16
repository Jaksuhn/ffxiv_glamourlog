using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.PrismBox;

// ATK buffer helpers for crystallize tree node; manage via LoadAtkValues.
internal readonly struct CrystallizeAtkSlot {
    internal bool IsLeaf { get; init; }
    internal AtkComponentTreeListItemType ItemType { get; init; }
    internal int SourceIndex { get; init; }
}

// flat AtkValue[] layout for node 11; captured from LoadAtkValues before parse/reload
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

    // caps InferItemCount when prior tab left stale slots — stop at first out-of-range leaf source index
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
            ClearTrailingSlots(atkPtr, layout, firstSlot, lastSlot);
    }

    private static bool SlotHasItemData(AtkValue[] atkValues, int baseIndex, int uintValuesPerItem) {
        for (var u = 0; u < uintValuesPerItem; u++) {
            if (atkValues[baseIndex + u].Type != AtkValueType.Undefined)
                return true;
        }

        return false;
    }

    // tree row (u0 = AtkComponentTreeListItemType) or legacy flat row (u0 = source index)
    internal static CrystallizeAtkSlot[] Parse(AtkValue[] atkValues, int itemCount, CrystallizeAtkBufferLayout layout, int categoryRowCount = 0) {
        if (itemCount <= 0)
            return [];

        var slots = new CrystallizeAtkSlot[itemCount];
        for (var slot = 0; slot < itemCount; slot++) {
            var baseIndex = layout.UintValuesOffset + slot * layout.UintValuesPerItem;
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

    // compact hidden slots in a working copy; returns new slot count (live buffer unchanged until handler reloads)
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
            ClearTrailingSlots(atkPtr, bufferLayout, keepSlots.Count, clearThrough);
        }

        return keepSlots.Count;
    }

    private static void ClearTrailingSlots(AtkValue* atkValues, CrystallizeAtkBufferLayout layout, int firstClearedSlot, int lastSlot) {
        var empty = default(AtkValue);
        var uintValuesOffset = layout.UintValuesOffset;
        var uintValuesPerItem = layout.UintValuesPerItem;
        for (var slot = firstClearedSlot; slot < lastSlot; slot++) {
            for (var u = 0; u < uintValuesPerItem; u++)
                atkValues[uintValuesOffset + slot * uintValuesPerItem + u] = empty;
        }
    }

    private static bool IsRealHeader(CrystallizeAtkSlot entry)
        => !entry.IsLeaf && entry.ItemType is AtkComponentTreeListItemType.GroupHeader or AtkComponentTreeListItemType.CollapsibleGroupHeader;

    private static bool IsHeaderType(uint value)
        => value is (uint)AtkComponentTreeListItemType.GroupHeader or (uint)AtkComponentTreeListItemType.CollapsibleGroupHeader;

    private static bool IsLeafType(uint value)
        => value is (uint)AtkComponentTreeListItemType.Leaf or (uint)AtkComponentTreeListItemType.LastLeafInGroup;

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

    internal static bool TryReadCategoryRow(AtkValue[] atkValues, int slot, CrystallizeAtkSlot entry, CrystallizeAtkBufferLayout layout, out PrismBoxCrystallizeItem row) {
        row = default;
        var baseIndex = layout.UintValuesOffset + slot * layout.UintValuesPerItem;
        if (baseIndex + 5 >= atkValues.Length || !entry.IsLeaf)
            return false;
        if (!TryReadCategoryRowFields(atkValues, slot, layout, IsTreeItemType(ReadUInt(atkValues, baseIndex)), out var inventory, out var itemSlot, out var itemId))
            return false;
        row = new PrismBoxCrystallizeItem {
            Inventory = inventory,
            Slot = itemSlot,
            ItemId = itemId,
        };
        return true;
    }

    internal static bool TryReadCategoryRowFromTreeItem(
        AtkComponentTreeListItem* item,
        CrystallizeAtkSlot entry,
        out PrismBoxCrystallizeItem row) {
        row = default;
        if (item is null || !entry.IsLeaf)
            return false;
        var uints = item->UIntValues;
        if (uints.Count < 4)
            return false;
        var isTreeLeaf = IsLeafType(uints[0]);
        if (!TryReadCategoryRowFieldsFromTreeItem(item, isTreeLeaf, out var inventory, out var itemSlot, out var itemId))
            return false;
        row = new PrismBoxCrystallizeItem {
            Inventory = inventory,
            Slot = itemSlot,
            ItemId = itemId,
        };
        return true;
    }

    private static bool TryReadCategoryRowFields(AtkValue[] atkValues, int slot, CrystallizeAtkBufferLayout layout, bool isTreeLeaf, out InventoryType inventory, out int itemSlot, out uint itemId) {
        inventory = default;
        itemSlot = 0;
        itemId = 0;
        var baseIndex = layout.UintValuesOffset + slot * layout.UintValuesPerItem;
        if (baseIndex + 5 >= atkValues.Length)
            return false;
        if (isTreeLeaf) {
            inventory = (InventoryType)ReadUInt(atkValues, baseIndex + 2);
            itemSlot = (int)ReadUInt(atkValues, baseIndex + 3);
            itemId = ReadUInt(atkValues, baseIndex + 4);
            if (itemId is 0 or uint.MaxValue)
                itemId = ReadUInt(atkValues, baseIndex + 5);
        }
        else {
            inventory = (InventoryType)ReadUInt(atkValues, baseIndex + 1);
            itemSlot = (int)ReadUInt(atkValues, baseIndex + 2);
            itemId = ReadUInt(atkValues, baseIndex + 3);
            if (itemId is 0 or uint.MaxValue)
                itemId = ReadUInt(atkValues, baseIndex + 4);
        }
        return itemId is not (0 or uint.MaxValue);
    }

    private static bool TryReadCategoryRowFieldsFromTreeItem(AtkComponentTreeListItem* item, bool isTreeLeaf, out InventoryType inventory, out int itemSlot, out uint itemId) {
        inventory = default;
        itemSlot = 0;
        itemId = 0;
        var uints = item->UIntValues;
        if (uints.Count < 4)
            return false;
        if (isTreeLeaf) {
            if (uints.Count < 5)
                return false;
            inventory = (InventoryType)uints[2];
            itemSlot = (int)uints[3];
            itemId = uints[4];
            if (itemId == 0 && uints.Count > 5)
                itemId = uints[5];
        }
        else {
            inventory = (InventoryType)uints[1];
            itemSlot = (int)uints[2];
            itemId = uints[3];
            if (itemId == 0 && uints.Count > 4)
                itemId = uints[4];
        }
        return itemId != 0;
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

    // keep headers when includeHeaders and section has a visible leaf (shouldExcludeSource, not shouldHideLeaf)
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

    // drop headers with no kept leaves after filtering
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

    // header check: skip shouldHideLeaf so duplicate suppression doesn't run here
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

    // after compaction, leaf u1 becomes 0..N-1 display index
    private static void WriteLeafIndex(AtkValue* atkValues, int uintValuesOffset, int uintValuesPerItem, int outSlot, int displayLeafIndex) {
        var baseIndex = uintValuesOffset + outSlot * uintValuesPerItem;
        if (IsTreeItemType(ReadUInt(atkValues, baseIndex))) {
            atkValues[baseIndex + 1].UInt = (uint)displayLeafIndex;
            return;
        }

        // flat row: rewrite u0 as tree leaf so it isn't a header type (2-4)
        atkValues[baseIndex].Type = AtkValueType.UInt;
        atkValues[baseIndex].UInt = (uint)AtkComponentTreeListItemType.Leaf;
        atkValues[baseIndex + 1].Type = AtkValueType.UInt;
        atkValues[baseIndex + 1].UInt = (uint)displayLeafIndex;
    }

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
