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

    private readonly DresserStoreScanner _scanner = new();

    protected override async Task Execute() {
        using var scope = BeginScope(nameof(StoreAllDresserTask));
        ErrorIf(!IsPrismBoxReady(), "Dresser not ready");

        while (true) {
            if (!_scanner.TryGetNextTarget(out var first))
                break;

            if (!TryGetMirageSetRow(first, out var setRow)) {
                var firstItemId = ItemUtil.GetBaseId(first.ItemId).ItemId;
                Svc.Log.Debug($"[DresserStore] skipping item #{firstItemId} because no matching set row was found");
                continue;
            }

            var group = _scanner.CollectStorablePiecesInSet(setRow);
            DresserStore.LogBatchDecision(setRow, group, first);

            if (DresserStore.CanBatchStore(setRow, group)) {
                Log($"Storing outfit batch ({group.Count} pieces) for set #{setRow.RowId}");
                await StorePieces(group, setRow);
                continue;
            }

            if (group.Count > 1) {
                Svc.Log.Debug($"[DresserStore] batch unavailable for set #{setRow.RowId}, storing {group.Count} pieces individually");
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

    private async Task StorePieces(IReadOnlyList<PrismBoxCrystallizeItem> rows, MirageStoreSetItem setRow) {
        using var scope = BeginScope(nameof(StorePieces));

        if (IsBatchStoreComplete(rows))
            return;

        var result = DresserStore.TrySendDirect(setRow, rows);
        ErrorIf(result.FilledCount == 0, "No store slots populated");
        ErrorIf(result.FilledCount < 0, "Client rejected dresser store");

        var sentPieces = result.SentPieces;
        Svc.Log.Debug(
            $"[DresserStore] sent {result.FilledCount} slot(s) directly for set #{setRow.RowId} " +
            $"(requested {rows.Count})");

        await WaitForStored(sentPieces);
        _scanner.MarkStored(sentPieces.Select(p => p.ItemId));

        await NextFrame(2);
    }

    private async Task WaitForStored(IReadOnlyList<PrismBoxCrystallizeItem> sentPieces) {
        using var scope = BeginScope("WaitForStored");
        const int timeoutMs = 30_000;
        var started = Environment.TickCount64;
        var deadline = started + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (IsBatchStoreComplete(sentPieces)) {
                Svc.Log.Debug($"[DresserStore] store confirmed in {Environment.TickCount64 - started}ms");
                return;
            }
            await NextFrame(1);
        }

        ErrorIf(true, "Timed out waiting for dresser store to complete");
    }

    private static bool IsBatchStoreComplete(IReadOnlyList<PrismBoxCrystallizeItem> rows)
        => rows.All(row => IsDresserStoreComplete(row, ItemUtil.GetBaseId(row.ItemId).ItemId));

    private static bool IsDresserStoreComplete(PrismBoxCrystallizeItem row, uint itemId) {
        itemId = ItemUtil.GetBaseId(row.ItemId != 0 ? row.ItemId : itemId).ItemId;
        if (itemId == 0)
            return true;

        if (row.Inventory != InventoryType.Invalid && DresserStore.IsSentSlotConsumed(row))
            return true;

        if (Svc.Get<OwnershipService>().IsCrystallizeItemFullyDeposited(itemId))
            return true;

        var handle = (ItemHandle)itemId;
        return !handle.TrySetItemLocation();
    }

    private static unsafe bool IsPrismBoxReady()
        => AtkUnitBase.IsAddonReady(Crystallize) && AtkUnitBase.IsAddonReady(PrismBox) && MirageManager.Instance()->PrismBoxLoaded;

    private static bool TryGetMirageSetRow(PrismBoxCrystallizeItem row, out MirageStoreSetItem setRow) {
        setRow = default;
        var baseId = ItemUtil.GetBaseId(row.ItemId).ItemId;
        if (baseId == 0)
            return false;

        foreach (var candidate in MirageStoreSetItem.Where(r => r.RowId > 0)) {
            if (!candidate.Items.Any(i => i.RowId != 0 && ItemUtil.GetBaseId(i.RowId).ItemId == baseId))
                continue;

            setRow = candidate;
            return true;
        }

        return false;
    }
}
