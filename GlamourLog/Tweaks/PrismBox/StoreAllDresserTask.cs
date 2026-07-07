using clib.TaskSystem;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed class StoreAllDresserTask : TaskBase {
    private const string Crystallize = "MiragePrismPrismBoxCrystallize";
    private const string PrismBox = "MiragePrismPrismBox";
    private const ushort FullCondition = 30000;
    private const int MaxStoreSlots = 9; // maybe this is stored in the struct somewhere

    private readonly HashSet<uint> _pendingStoredBaseIds = [];
    private readonly HashSet<uint> _visitedInventoryBaseIds = [];

    protected override async Task Execute() {
        using var scope = BeginScope(nameof(StoreAllDresserTask));
        ErrorIf(!IsPrismBoxReady(), "Dresser not ready");

        while (true) {
            if (!TryGetNextTarget(out var first))
                break;

            if (!MirageStoreSetItemLookup.TryGetRow(ItemUtil.GetBaseId(first.ItemId).ItemId, out var lookup)) {
                Log($"skipping item #{first.ItemId} - no matching set row was found");
                continue;
            }

            var setRow = MirageStoreSetItem.GetRow(lookup.RowId);
            var group = CollectStorablePiecesInSet(setRow);
            var batchable = CountBatchableSetPieces(setRow, group);
            Log($"set=#{setRow.RowId} first=#{ItemUtil.GetBaseId(first.ItemId).ItemId} batchable={batchable} group={group.Count} canBatch={batchable >= 2}");

            if (CanBatchStore(setRow, group)) {
                Log($"Storing outfit batch ({group.Count} pieces) for set #{setRow.RowId}");
                await StorePieces(group, setRow);
                continue;
            }

            if (group.Count > 1) {
                Log($"batch unavailable for set #{setRow.RowId}, storing {group.Count} pieces individually");
                foreach (var piece in group) {
                    var pieceId = ItemUtil.GetBaseId(piece.ItemId).ItemId;
                    if (pieceId == 0 || IsDresserStoreComplete(piece, pieceId))
                        continue;

                    Log($"Storing dresser item #{pieceId}");
                    await StorePieces([piece], setRow);
                    await NextFrame(2);
                }
                continue;
            }

            var itemId = ItemUtil.GetBaseId(first.ItemId).ItemId;
            Log($"Storing dresser item #{itemId}");
            await StorePieces([first], setRow);
        }
    }

    private async Task StorePieces(List<PrismBoxCrystallizeItem> rows, MirageStoreSetItem setRow) {
        using var scope = BeginScope(nameof(StorePieces));

        if (IsBatchStoreComplete(rows))
            return;

        var result = TrySendDirect(setRow, rows, Log);
        ErrorIf(result.FilledCount == 0, "No store slots populated");
        ErrorIf(result.FilledCount < 0, "Store rejected");

        var sentPieces = result.SentPieces;
        Log($"Stored {result.FilledCount} slot(s) for set #{setRow.RowId} (requested {rows.Count})");

        await WaitForStored(sentPieces);
        MarkStored(sentPieces.Select(p => p.ItemId));

        await NextFrame(2);
    }

    private async Task WaitForStored(IReadOnlyList<PrismBoxCrystallizeItem> sentPieces) {
        using var scope = BeginScope("WaitForStored");
        const int timeoutMs = 30_000;
        var started = Environment.TickCount64;
        var deadline = started + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (IsBatchStoreComplete(sentPieces)) {
                Log($"Store confirmed in {Environment.TickCount64 - started}ms");
                return;
            }
            await NextFrame(1);
        }

        ErrorIf(true, "Timed out waiting for dresser store to complete");
    }

    private void MarkStored(IEnumerable<uint> itemIds) {
        foreach (var itemId in itemIds) {
            var baseId = ItemUtil.GetBaseId(itemId).ItemId;
            if (baseId != 0) {
                _pendingStoredBaseIds.Add(baseId);
                _visitedInventoryBaseIds.Add(baseId);
            }
        }

        itemIds.ForEach(id => Svc.Get<CrystallizeListHandler>().NotifyItemStored(id));
        PrunePendingStored();
    }

    private bool TryGetNextTarget(out PrismBoxCrystallizeItem item) {
        item = default;
        PrunePendingStored();
        _visitedInventoryBaseIds.Clear();

        foreach (var set in Svc.Get<CatalogService>().GlamourSets) {
            foreach (var pieceId in set.Items) {
                if (pieceId == 0)
                    continue;

                var baseId = ItemUtil.GetBaseId(pieceId).ItemId;
                if (baseId == 0 || _visitedInventoryBaseIds.Contains(baseId) || _pendingStoredBaseIds.Contains(baseId))
                    continue;

                if (!TryCreateStorableRow(pieceId, out var row))
                    continue;

                _visitedInventoryBaseIds.Add(baseId);
                item = row;
                RefreshInventoryLocation(ref item);
                return true;
            }
        }

        return false;
    }

    private List<PrismBoxCrystallizeItem> CollectStorablePiecesInSet(MirageStoreSetItem setRow)
        => [.. setRow.Items.Where(p => p.RowId != 0)
            .Select(p => {
                if (!TryCreateStorableRow(p.RowId, out var row))
                    return default;
                RefreshInventoryLocation(ref row);
                return row;
            }).Where(r => r.ItemId != 0)];

    private void PrunePendingStored() {
        if (_pendingStoredBaseIds.Count == 0)
            return;

        var ownership = Svc.Get<OwnershipService>();
        _pendingStoredBaseIds.RemoveWhere(ownership.IsCrystallizeItemFullyDeposited);
    }

    private static bool CanStore(uint itemId) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0)
            return false;

        var ownership = Svc.Get<OwnershipService>();
        return !ownership.IsCrystallizeItemFullyDeposited(baseId) && !ownership.IsArmoireEligible(baseId) && HasSufficientCondition(itemId);
    }

    private static bool TryCreateStorableRow(uint itemId, out PrismBoxCrystallizeItem row) {
        row = default;
        if (!CanStore(itemId))
            return false;

        var handle = (ItemHandle)itemId;
        if (!handle.TrySetItemLocation())
            return false;

        row = new PrismBoxCrystallizeItem {
            ItemId = itemId,
            Inventory = handle.ItemLocation.Container,
            Slot = handle.ItemLocation.Slot,
        };
        return true;
    }

    private static unsafe bool HasSufficientCondition(uint itemId) {
        var handle = (ItemHandle)itemId;
        if (!handle.TrySetItemLocation())
            return false;

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return false;

        var item = inventory->GetInventorySlot(handle.ItemLocation.Container, handle.ItemLocation.Slot);
        return item is not null && item->ItemId != 0 && item->GetCondition() >= FullCondition;
    }

    private static bool IsBatchStoreComplete(IReadOnlyList<PrismBoxCrystallizeItem> rows)
        => rows.All(row => IsDresserStoreComplete(row, ItemUtil.GetBaseId(row.ItemId).ItemId));

    private static bool IsDresserStoreComplete(PrismBoxCrystallizeItem row, uint itemId) {
        itemId = ItemUtil.GetBaseId(row.ItemId != 0 ? row.ItemId : itemId).ItemId;
        if (itemId == 0)
            return true;

        if (row.Inventory != InventoryType.Invalid && IsSentSlotConsumed(row))
            return true;

        if (Svc.Get<OwnershipService>().IsCrystallizeItemFullyDeposited(itemId))
            return true;

        var handle = (ItemHandle)itemId;
        return !handle.TrySetItemLocation();
    }

    private static unsafe bool IsPrismBoxReady()
        => AtkUnitBase.IsAddonReady(Crystallize) && AtkUnitBase.IsAddonReady(PrismBox) && MirageManager.Instance()->PrismBoxLoaded;

    private static unsafe bool IsSentSlotConsumed(PrismBoxCrystallizeItem row) {
        if (row.Inventory == InventoryType.Invalid)
            return true;

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return true;

        var item = inventory->GetInventorySlot(row.Inventory, row.Slot);
        if (item is null || item->ItemId == 0)
            return true;

        var expected = ItemUtil.GetBaseId(row.ItemId).ItemId;
        return expected == 0 || ItemUtil.GetBaseId(item->ItemId).ItemId != expected;
    }

    private static void RefreshInventoryLocation(ref PrismBoxCrystallizeItem row) {
        if (row.ItemId == 0)
            return;
        var handle = (ItemHandle)row.ItemId;
        if (!handle.TrySetItemLocation())
            return;
        row.Inventory = handle.ItemLocation.Container;
        row.Slot = handle.ItemLocation.Slot;
    }

    private static bool CanBatchStore(MirageStoreSetItem setRow, IReadOnlyList<PrismBoxCrystallizeItem> pieces)
        => CountBatchableSetPieces(setRow, pieces) >= 2;

    private static int CountBatchableSetPieces(MirageStoreSetItem setRow, IReadOnlyList<PrismBoxCrystallizeItem> pieces) {
        var setPieceIds = setRow.Items.Where(item => item.RowId != 0).Select(item => ItemUtil.GetBaseId(item.RowId).ItemId).Where(id => id != 0).ToHashSet();
        return pieces.Count(p => {
            var id = ItemUtil.GetBaseId(p.ItemId).ItemId;
            return id != 0 && setPieceIds.Contains(id);
        });
    }

    private readonly struct DirectStoreResult {
        internal int FilledCount { get; init; }
        internal IReadOnlyList<PrismBoxCrystallizeItem> SentPieces { get; init; }
    }

    private static unsafe DirectStoreResult TrySendDirect(MirageStoreSetItem setRow, IReadOnlyList<PrismBoxCrystallizeItem> pieces, Action<string> log) {
        var mirage = MirageManager.Instance();
        if (mirage is null)
            return default;

        var pieceByBaseId = BuildPieceMap(pieces);
        var storeIntoExisting = TryFindPrismBoxIndex(mirage, setRow.RowId, out var prismBoxIndex);

        Span<InventoryType> containers = stackalloc InventoryType[MaxStoreSlots];
        Span<ushort> slots = stackalloc ushort[MaxStoreSlots];
        containers.Fill(InventoryType.Invalid);
        slots.Clear();

        var filledCount = 0;
        List<PrismBoxCrystallizeItem>? sentPieces = null;
        var sheetSlotLimit = Math.Min(setRow.Items.Count, Svc.Data.GetSheet<MirageStoreSetItem>().Columns.Count);
        for (var sheetSlot = 0; sheetSlot < sheetSlotLimit; sheetSlot++) {
            if (filledCount >= MaxStoreSlots)
                break;

            var itemRef = setRow.Items[sheetSlot];
            if (itemRef.RowId == 0)
                continue;

            if (storeIntoExisting && setRow.IsSetSlotCollected(sheetSlot))
                continue;

            var requiredBaseId = ItemUtil.GetBaseId(itemRef.RowId).ItemId;
            if (!pieceByBaseId.TryGetValue(requiredBaseId, out var row) || row.Inventory == InventoryType.Invalid)
                continue;

            containers[filledCount] = row.Inventory;
            slots[filledCount] = (ushort)row.Slot;
            sentPieces ??= [with(MaxStoreSlots)];
            sentPieces.Add(row);
            filledCount++;
        }

        if (filledCount == 0)
            return default;

        LogPackedStoreRequest(log, setRow.RowId, storeIntoExisting, prismBoxIndex, containers, slots, filledCount);

        fixed (InventoryType* containerPtr = containers)
        fixed (ushort* slotPtr = slots) {
            var sent = storeIntoExisting ? mirage->StoreExistingOutfit(prismBoxIndex, containerPtr, slotPtr) : mirage->StoreNewOutfit(setRow.RowId, containerPtr, slotPtr);
            return new DirectStoreResult {
                FilledCount = sent ? filledCount : -1,
                SentPieces = sentPieces ?? [],
            };
        }
    }

    private static void LogPackedStoreRequest(Action<string> Log, uint setRowId, bool storeIntoExisting, uint prismBoxIndex, ReadOnlySpan<InventoryType> containers, ReadOnlySpan<ushort> slots, int filledCount) {
        var target = storeIntoExisting ? $"existing#{prismBoxIndex}" : $"new#{setRowId}";
        var packedParts = new List<string>(filledCount);
        for (var i = 0; i < filledCount; i++)
            packedParts.Add($"{containers[i]}:{slots[i]}");

        var padParts = new List<string>(MaxStoreSlots - filledCount);
        for (var i = filledCount; i < MaxStoreSlots; i++)
            padParts.Add($"{containers[i]}:{slots[i]}");

        Log($"[DresserStore] packed store for {target}: [{string.Join(", ", packedParts)}] pad=[{string.Join(", ", padParts)}]");
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

    private static unsafe bool TryFindPrismBoxIndex(MirageManager* mirage, uint setItemId, out uint index) {
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
