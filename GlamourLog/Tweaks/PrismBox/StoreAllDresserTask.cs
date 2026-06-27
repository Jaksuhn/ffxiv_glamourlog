using clib.TaskSystem;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using Lumina.Excel.Sheets;
using System.Threading.Tasks;

namespace GlamourLog.Features.PrismBox;

internal sealed class StoreAllDresserTask : TaskBase {
    private const string Crystallize = "MiragePrismPrismBoxCrystallize";
    private const string PrismBox = "MiragePrismPrismBox";
    private const int CategoryCount = 6;

    private readonly DresserStoreScanner _scanner = new();

    protected override async Task Execute() {
        ErrorIf(!IsPrismBoxReady(), "Dresser not ready");
        for (var categoryIndex = 0; categoryIndex < CategoryCount; categoryIndex++) {
            await SelectCategory(categoryIndex);
            await StoreAllInCurrentCategory();
        }
    }

    private async Task SelectCategory(int categoryIndex) {
        using var scope = BeginScope(nameof(SelectCategory));
        ErrorIf(!_scanner.TrySelectCategory(categoryIndex), "Failed to switch category");

        Log($"Switching to category {categoryIndex + 1}/{CategoryCount}");
        await WaitUntil(() => _scanner.IsCategoryTabAligned(categoryIndex), "WaitForCategoryTab");
        await WaitUntil(() => _scanner.IsCategoryReady(categoryIndex), "WaitForCategoryReady");
        await NextFrame(2);
    }

    private async Task StoreAllInCurrentCategory() {
        using var scope = BeginScope(nameof(StoreAllInCurrentCategory));
        if (!_scanner.IsCategoryReady(GetCurrentCategoryIndex()))
            await WaitUntil(() => _scanner.IsCategoryReady(GetCurrentCategoryIndex()), "WaitForCategoryReady");

        while (true) {
            if (!TryGetNextDresserStoreTarget(out var targets, out var setRow))
                break;
            var group = targets.Count > 1 ? targets : _scanner.CollectStorablePiecesInSet(setRow);
            DresserStore.LogBatchDecision(setRow, group, targets[0]);

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

            var itemId = ItemUtil.GetBaseId(targets[0].ItemId).ItemId;
            Log($"Storing dresser item #{itemId}");
            await StorePieces([targets[0]], setRow);
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
        Svc.Get<CrystallizeListHandler>().NotifyItemsStored(sentPieces.Select(p => p.ItemId));

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

    private bool TryGetNextDresserStoreTarget(
        out IReadOnlyList<PrismBoxCrystallizeItem> targets,
        out MirageStoreSetItem setRow) {
        targets = [];
        setRow = default;
        if (!_scanner.TryGetNextTarget(out var first))
            return false;

        if (!TryGetMirageSetRow(first, out setRow))
            return false;

        var group = _scanner.CollectStorablePiecesInSet(setRow);
        if (group.Count > 1 && DresserStore.CanBatchStore(setRow, group)) {
            targets = group;
            return true;
        }

        targets = [first];
        return true;
    }

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

    private static unsafe int GetCurrentCategoryIndex() {
        var data = GetData();
        return data is null ? -1 : data->CrystallizeCategory;
    }

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }
}
