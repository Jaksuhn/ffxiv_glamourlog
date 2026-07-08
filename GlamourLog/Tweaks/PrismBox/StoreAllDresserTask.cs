using clib.TaskSystem;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed class StoreAllDresserTask : TaskBase {
    private const string Crystallize = "MiragePrismPrismBoxCrystallize";
    private const string PrismBox = "MiragePrismPrismBox";
    private const ushort FullCondition = 30000;
    private const int MaxStoreSlots = 9;

    private readonly HashSet<uint> _pendingStoredBaseIds = [];
    private readonly HashSet<uint> _visitedInventoryBaseIds = [];

    private readonly struct SetScan(MirageStoreSetItem row, IReadOnlyList<uint> outfits, HashSet<uint> looseDresser) {
        internal MirageStoreSetItem Row => row;
        internal IReadOnlyList<uint> Outfits => outfits;
        internal HashSet<uint> LooseDresser => looseDresser;
    }

    protected override async Task Execute() {
        using var scope = BeginScope(nameof(StoreAllDresserTask));
        using var deferCrystallizeRefresh = Svc.Get<CrystallizeListHandler>().DeferRefresh();
        ErrorIf(!IsPrismBoxReady(), "Dresser not ready");

        while (true) {
            await NextFrame(1);
            if (!TryGetNextTarget(out var catalogSet, out var first, out var scan))
                break;

            var setName = catalogSet.Name;
            var group = CollectStorablePiecesInSet(scan);
            var batchable = CountBatchableSetPieces(scan.Row, group);
            Log($"set=#{scan.Row.RowId} first=#{ItemUtil.GetBaseId(first.ItemId).ItemId} batchable={batchable} group={group.Count} canBatch={batchable >= 2}");

            if (CanBatchStore(scan.Row, group)) {
                Log($"Storing outfit batch ({group.Count} pieces) for set {setName}#{scan.Row.RowId}");
                await StorePieces(group, scan, setName);
                continue;
            }

            if (group.Count > 1) {
                Log($"batch unavailable for set #{scan.Row.RowId}, storing {group.Count} pieces individually");
                foreach (var piece in group) {
                    var pieceId = ItemUtil.GetBaseId(piece.ItemId).ItemId;
                    if (pieceId == 0 || IsDresserStoreComplete(piece, pieceId))
                        continue;

                    Log($"Storing dresser item #{pieceId}");
                    await StorePieces([piece], scan, setName);
                    await NextFrame(2);
                }
                continue;
            }

            Log($"Storing dresser item #{ItemUtil.GetBaseId(first.ItemId).ItemId}");
            await StorePieces([first], scan, setName);
        }
    }

    private async Task StorePieces(List<PrismBoxCrystallizeItem> rows, SetScan scan, string setName) {
        using var scope = BeginScope(nameof(StorePieces));

        if (rows.All(IsSentSlotConsumed))
            return;

        var result = TrySendDirect(scan, rows);
        ErrorIf(result.FilledCount == 0, "No store slots populated");
        ErrorIf(result.FilledCount < 0, "Store rejected");

        Log($"Stored {result.FilledCount} slot(s) for set {setName}#{scan.Row.RowId} (requested {rows.Count})");
        await WaitForStored(result.SentPieces);
        MarkStored(result.SentPieces.Select(p => p.ItemId));

        var builder = new SeStringBuilder()
            .Append($"Stored {result.FilledCount} item{(result.FilledCount is 1 ? string.Empty : 's')} in ")
            .AddUiForeground(549).AddUiGlow(550)
            .Append($"{setName}")
            .AddUiGlowOff().AddUiForegroundOff();
        Svc.Toasts.ShowQuest(builder.BuiltString);
        await NextFrame(2);
    }

    private async Task WaitForStored(IReadOnlyList<PrismBoxCrystallizeItem> sentPieces) {
        using var scope = BeginScope("WaitForStored");
        const int timeoutMs = 30_000;
        var started = Environment.TickCount64;
        var deadline = started + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (sentPieces.All(IsSentSlotConsumed)) {
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

    private unsafe bool TryGetNextTarget(out GlamourSet catalogSet, out PrismBoxCrystallizeItem item, out SetScan scan) {
        catalogSet = null!;
        item = default;
        scan = default;
        PrunePendingStored();
        _visitedInventoryBaseIds.Clear();

        HashSet<uint>? looseDresser = null;
        foreach (var set in Svc.Get<CatalogService>().GlamourSets) {
            if (!MirageStoreSetItem.TryGetRow(set.ItemId, out var setRow))
                continue;

            looseDresser ??= BuildLooseDresserIdSet();
            var setScan = new SetScan(setRow, CollectOutfitIndices(setRow.RowId), looseDresser);
            foreach (var pieceId in set.Items) {
                if (pieceId == 0)
                    continue;

                var baseId = ItemUtil.GetBaseId(pieceId).ItemId;
                if (baseId == 0 || _visitedInventoryBaseIds.Contains(baseId) || _pendingStoredBaseIds.Contains(baseId))
                    continue;

                if (!TryCreateStorableRow(pieceId, setScan, out var row))
                    continue;

                _visitedInventoryBaseIds.Add(baseId);
                item = row;
                catalogSet = set;
                scan = setScan;
                RefreshInventoryLocation(ref item);
                return true;
            }
        }

        return false;
    }

    private static List<PrismBoxCrystallizeItem> CollectStorablePiecesInSet(SetScan scan)
        => [.. scan.Row.Items.Where(p => p.RowId != 0)
            .Select(p => {
                if (!TryCreateStorableRow(p.RowId, scan, out var row))
                    return default;
                RefreshInventoryLocation(ref row);
                return row;
            }).Where(r => r.ItemId != 0)];

    private void PrunePendingStored() {
        if (_pendingStoredBaseIds.Count == 0)
            return;

        _pendingStoredBaseIds.RemoveWhere(baseId => {
            var handle = (ItemHandle)baseId;
            return !handle.TrySetItemLocation();
        });
    }

    private static bool TryCreateStorableRow(uint itemId, SetScan scan, out PrismBoxCrystallizeItem row) {
        row = default;
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        if (baseId == 0
            || Svc.Get<OwnershipService>().IsArmoireEligible(baseId)
            || !HasSufficientCondition(itemId)
            || scan.LooseDresser.Contains(baseId)
            || !HasUnsetSlotForPiece(scan.Row, baseId, scan.Outfits))
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

    private static unsafe HashSet<uint> BuildLooseDresserIdSet() {
        var finder = ItemFinderModule.Instance();
        if (finder is null)
            return [];

        var ids = new HashSet<uint>();
        foreach (var id in finder->GlamourDresserBaseItemIds) {
            if (id != 0)
                ids.Add(id);
        }

        return ids;
    }

    private static unsafe List<uint> CollectOutfitIndices(uint setItemId) {
        var mirage = MirageManager.Instance();
        if (mirage is null)
            return [];

        var ids = mirage->PrismBoxItemIds;
        var result = new List<uint>(1);
        for (var i = 0; i < ids.Length; i++) {
            if (ids[i] == setItemId)
                result.Add((uint)i);
        }

        return result;
    }

    private static unsafe bool HasUnsetSlotForPiece(MirageStoreSetItem setRow, uint pieceBaseId, IReadOnlyList<uint> outfitIndices) {
        var mirage = MirageManager.Instance();
        if (mirage is null)
            return true;

        int? pieceSheetSlot = null;
        foreach (var (slotIndex, itemRef) in setRow.Items.Index()) {
            if (itemRef.RowId != 0 && ItemUtil.GetBaseId(itemRef.RowId).ItemId == pieceBaseId) {
                pieceSheetSlot = slotIndex;
                break;
            }
        }

        if (pieceSheetSlot is null)
            return false;
        if (outfitIndices.Count == 0)
            return true;

        foreach (var outfitIndex in outfitIndices) {
            if (!mirage->IsSetSlotUnlocked(outfitIndex, pieceSheetSlot.Value))
                return true;
        }

        return false;
    }

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

    private readonly struct StoreResult {
        internal int FilledCount { get; init; }
        internal IReadOnlyList<PrismBoxCrystallizeItem> SentPieces { get; init; }
    }

    private static unsafe StoreResult TrySendDirect(SetScan scan, IReadOnlyList<PrismBoxCrystallizeItem> pieces) {
        var mirage = MirageManager.Instance();
        if (mirage is null)
            return default;

        var setRow = scan.Row;
        var pieceByBaseId = BuildPieceMap(pieces);
        var storeIntoExisting = TryFindPrismBoxIndexForStore(mirage, setRow, pieceByBaseId, scan.Outfits, out var prismBoxIndex);

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
            if (storeIntoExisting && mirage->IsSetSlotUnlocked(prismBoxIndex, sheetSlot))
                continue;

            var requiredBaseId = ItemUtil.GetBaseId(itemRef.RowId).ItemId;
            if (!pieceByBaseId.TryGetValue(requiredBaseId, out var row) || row.Inventory == InventoryType.Invalid)
                continue;

            containers[filledCount] = row.Inventory;
            slots[filledCount] = (ushort)row.Slot;
            sentPieces ??= [];
            sentPieces.Add(row);
            filledCount++;
        }

        if (filledCount == 0)
            return default;

        fixed (InventoryType* containerPtr = containers)
        fixed (ushort* slotPtr = slots) {
            var sent = storeIntoExisting ? mirage->StoreExistingOutfit(prismBoxIndex, containerPtr, slotPtr) : mirage->StoreNewOutfit(setRow.RowId, containerPtr, slotPtr);
            return new StoreResult {
                FilledCount = sent ? filledCount : -1,
                SentPieces = sentPieces ?? [],
            };
        }
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

    private static unsafe bool TryFindPrismBoxIndexForStore(MirageManager* mirage, MirageStoreSetItem setRow, Dictionary<uint, PrismBoxCrystallizeItem> pieceByBaseId, IReadOnlyList<uint> outfitIndices, out uint index) {
        index = 0;
        var sheetSlotLimit = Math.Min(setRow.Items.Count, Svc.Data.GetSheet<MirageStoreSetItem>().Columns.Count);

        foreach (var outfitIndex in outfitIndices) {
            for (var sheetSlot = 0; sheetSlot < sheetSlotLimit; sheetSlot++) {
                var itemRef = setRow.Items[sheetSlot];
                if (itemRef.RowId == 0 || mirage->IsSetSlotUnlocked(outfitIndex, sheetSlot))
                    continue;

                var baseId = ItemUtil.GetBaseId(itemRef.RowId).ItemId;
                if (baseId != 0 && pieceByBaseId.ContainsKey(baseId)) {
                    index = outfitIndex;
                    return true;
                }
            }
        }

        return false;
    }
}
