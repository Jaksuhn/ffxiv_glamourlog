using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamourLog.Features.PrismBox;

internal static unsafe class DresserStore {
    /// <summary>MirageManager store packets hold 9 parallel slots (head through ring).</summary>
    private const int MaxStoreSlots = 9;

    /// <summary>
    /// MirageStoreSetItem sheet columns: 0 main hand, 1 off hand, 2 head, 3 body, 4 gloves,
    /// 5 legs, 6 feet, 7 earring, 8 choker, 9 bracelet, 10 ring. Weapon columns are unused for outfits.
    /// </summary>
    private const int MirageStoreSetSheetSlotCount = 11;

    /// <summary>True when the inventory slot we sent is empty or no longer holds the piece.</summary>
    internal static bool IsSentSlotConsumed(PrismBoxCrystallizeItem row) {
        if (row.Inventory == InventoryType.Invalid)
            return true;

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return true;

        var item = inventory->GetInventorySlot(row.Inventory, row.Slot);
        if (item is null || item->ItemId == 0)
            return true;

        var expected = ItemUtil.GetBaseId(row.ItemId).ItemId;
        if (expected == 0)
            return true;

        return ItemUtil.GetBaseId(item->ItemId).ItemId != expected;
    }

    internal static void RefreshInventoryLocation(ref PrismBoxCrystallizeItem row) {
        if (row.ItemId == 0)
            return;
        var handle = (ItemHandle)row.ItemId;
        if (!handle.TrySetItemLocation())
            return;
        row.Inventory = handle.ItemLocation.Container;
        row.Slot = handle.ItemLocation.Slot;
    }

    internal static bool CanBatchStore(MirageStoreSetItem setRow, IReadOnlyList<PrismBoxCrystallizeItem> pieces)
        => CountBatchableSetPieces(setRow, pieces) >= 2;

    internal static int CountBatchableSetPieces(MirageStoreSetItem setRow, IReadOnlyList<PrismBoxCrystallizeItem> pieces) {
        var setPieceIds = setRow.Items
            .Where(item => item.RowId != 0)
            .Select(item => ItemUtil.GetBaseId(item.RowId).ItemId)
            .Where(id => id != 0)
            .ToHashSet();

        return pieces.Count(p => {
            var id = ItemUtil.GetBaseId(p.ItemId).ItemId;
            return id != 0 && setPieceIds.Contains(id);
        });
    }

    internal static void LogBatchDecision(
        MirageStoreSetItem setRow,
        IReadOnlyList<PrismBoxCrystallizeItem> group,
        PrismBoxCrystallizeItem first) {
        var batchable = CountBatchableSetPieces(setRow, group);
        var firstId = ItemUtil.GetBaseId(first.ItemId).ItemId;
        Svc.Log.Debug(
            $"[DresserStore] set=#{setRow.RowId} first=#{firstId} batchable={batchable} " +
            $"group={group.Count} canBatch={CanBatchStore(setRow, group)}");
    }

    internal readonly struct DirectStoreResult {
        internal int FilledCount { get; init; }
        internal IReadOnlyList<PrismBoxCrystallizeItem> SentPieces { get; init; }
    }

    /// <returns>filled slot count, or -1 if MirageManager rejected the store</returns>
    internal static DirectStoreResult TrySendDirect(MirageStoreSetItem setRow, IReadOnlyList<PrismBoxCrystallizeItem> pieces) {
        var mirage = MirageManager.Instance();
        if (mirage is null)
            return default;

        var pieceByBaseId = BuildPieceMap(pieces);
        var storeIntoExisting = TryFindPrismBoxIndex(mirage, setRow.RowId, out var prismBoxIndex);

        // AgentMiragePrismPrismSetConvert ReceiveEvent (confirm) packs AgentData.Items[0..NumItemsInSet)
        // into parallel 9-element arrays with no holes, padding the tail with Invalid/0.
        Span<InventoryType> containers = stackalloc InventoryType[MaxStoreSlots];
        Span<ushort> slots = stackalloc ushort[MaxStoreSlots];
        containers.Fill(InventoryType.Invalid);
        slots.Clear();

        var filledCount = 0;
        List<PrismBoxCrystallizeItem>? sentPieces = null;
        var sheetSlotLimit = Math.Min(setRow.Items.Count, MirageStoreSetSheetSlotCount);
        for (var sheetSlot = 0; sheetSlot < sheetSlotLimit; sheetSlot++) {
            if (filledCount >= MaxStoreSlots)
                break;

            var itemRef = setRow.Items[sheetSlot];
            if (itemRef.RowId == 0)
                continue;

            if (storeIntoExisting && setRow.IsSetSlotCollected(sheetSlot))
                continue;

            var requiredBaseId = ItemUtil.GetBaseId(itemRef.RowId).ItemId;
            if (!pieceByBaseId.TryGetValue(requiredBaseId, out var row)
                || row.Inventory == InventoryType.Invalid)
                continue;

            containers[filledCount] = row.Inventory;
            slots[filledCount] = (ushort)row.Slot;
            sentPieces ??= [with(MaxStoreSlots)];
            sentPieces.Add(row);
            filledCount++;
        }

        if (filledCount == 0)
            return default;

        LogPackedStoreRequest(setRow.RowId, storeIntoExisting, prismBoxIndex, containers, slots, filledCount);

        fixed (InventoryType* containerPtr = containers)
        fixed (ushort* slotPtr = slots) {
            var sent = storeIntoExisting
                ? mirage->StoreExistingOutfit(prismBoxIndex, containerPtr, slotPtr)
                : mirage->StoreNewOutfit(setRow.RowId, containerPtr, slotPtr);
            return new DirectStoreResult {
                FilledCount = sent ? filledCount : -1,
                SentPieces = sentPieces ?? [],
            };
        }
    }

    private static void LogPackedStoreRequest(
        uint setRowId,
        bool storeIntoExisting,
        uint prismBoxIndex,
        ReadOnlySpan<InventoryType> containers,
        ReadOnlySpan<ushort> slots,
        int filledCount) {
        var target = storeIntoExisting ? $"existing#{prismBoxIndex}" : $"new#{setRowId}";
        var packedParts = new List<string>(filledCount);
        for (var i = 0; i < filledCount; i++)
            packedParts.Add($"{containers[i]}:{slots[i]}");

        var padParts = new List<string>(MaxStoreSlots - filledCount);
        for (var i = filledCount; i < MaxStoreSlots; i++)
            padParts.Add($"{containers[i]}:{slots[i]}");

        Svc.Log.Debug(
            $"[DresserStore] packed store for {target}: [{string.Join(", ", packedParts)}] " +
            $"pad=[{string.Join(", ", padParts)}]");
    }

    private static Dictionary<uint, PrismBoxCrystallizeItem> BuildPieceMap(IReadOnlyList<PrismBoxCrystallizeItem> pieces) {
        var pieceByBaseId = new Dictionary<uint, PrismBoxCrystallizeItem>();
        foreach (var piece in pieces) {
            var baseId = ItemUtil.GetBaseId(piece.ItemId).ItemId;
            if (baseId == 0)
                continue;

            var row = piece;
            RefreshInventoryLocation(ref row);
            pieceByBaseId[baseId] = row;
        }

        return pieceByBaseId;
    }

    private static bool TryFindPrismBoxIndex(MirageManager* mirage, uint setItemId, out uint index) {
        index = 0;
        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++) {
            if (ids[i] != setItemId)
                continue;

            index = (uint)i;
            return true;
        }

        return false;
    }
}
