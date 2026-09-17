using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Threading.Tasks;

namespace GlamourLog.Tweaks.PrismBox;

internal sealed partial class CrystallizeListHandler : ListHandlerBase, IPluginService, IAsyncDisposable {
    internal const string AddonName = "MiragePrismPrismBoxCrystallize";

    // semi guesses based on what the gearset filter does in LABEL_109
    private const int CrystallizeItemActionPending = 0x11AE7D; // when set, update opens crystallize confirm for CrystallizeItemIndex instead of scanning; clear so filter/category kicks don't stay in that path
    private const int CrystallizeSectionSlot = 0x11AE80; // last equip-slot group that got a tree section header (-1 = none); reset so headers rebuild from scratch
    private const int CrystallizePopulateIndex = 0x11AE8E; // cursor into CrystallizeItems while building AtkValues; 0 = start of a fresh rebuild (mid-build resumes leave this > 0)

    private readonly Hook<AgentMiragePrismPrismBox.Delegates.PopulateCrystallizeAndFireRefresh>? _populateHook;

    private int _deferredRefreshDepth;
    private bool _repopulateScheduled;

    public unsafe CrystallizeListHandler() : base(12, PrismBoxFilters.Create()) {
        _populateHook = IGameInteropProvider.Get().HookFromAddress<AgentMiragePrismPrismBox.Delegates.PopulateCrystallizeAndFireRefresh>(
            (nint)AgentMiragePrismPrismBox.MemberFunctionPointers.PopulateCrystallizeAndFireRefresh,
            PopulateCrystallizeAndFireRefreshDetour);
        _populateHook.Enable();

        IGameInventory.Get().InventoryChanged += OnInventoryChanged;
    }

    internal void OnConfigChanged() => IFramework.Get().Run(ApplyConfigChange);

    internal IDisposable DeferRefresh() {
        _deferredRefreshDepth++;
        return new DeferredRefreshScope(this);
    }

    internal unsafe void NotifyItemStored(uint itemId) {
        if (ItemUtil.GetBaseId(itemId).ItemId == 0 || _deferredRefreshDepth > 0)
            return;

        QueueNativeRepopulate($"stored item {ItemUtil.GetBaseId(itemId).ItemId}");
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events) {
        if (!IsFilteringActive || _deferredRefreshDepth > 0)
            return;

        foreach (var eventData in events) {
            if (eventData is not (InventoryItemRemovedArgs or InventoryItemAddedArgs))
                continue;
            if (!InventoryType.AllPlayer.Contains((InventoryType)eventData.Item.ContainerType))
                continue;

            QueueNativeRepopulate(eventData is InventoryItemRemovedArgs ? "inventory item removed" : "inventory item added");
            return;
        }
    }

    private unsafe void ApplyConfigChange() {
        var data = GetData();
        if (data is null)
            return;

        LogFilterDebug(nameof(ApplyConfigChange), IsFilteringActive
            ? $"filters enabled [{DescribeEnabledFilters()}]; kicking native repopulate"
            : "filters disabled; kicking native repopulate");
        KickNativeRepopulate(data);
    }

    private unsafe void QueueNativeRepopulate(string reason) {
        if (_deferredRefreshDepth > 0 || _repopulateScheduled)
            return;

        _repopulateScheduled = true;
        LogFilterDebug(nameof(QueueNativeRepopulate), reason);

        IFramework.Get().RunOnTick(() => {
            _repopulateScheduled = false;
            if (_deferredRefreshDepth > 0)
                return;

            var data = GetData();
            if (data is null)
                return;

            KickNativeRepopulate(data);
        }, delayTicks: 1);
    }

    private void FlushDeferredRefresh() {
        if (--_deferredRefreshDepth > 0)
            return;

        IFramework.Get().Run(() => QueueNativeRepopulate("deferred store-all flush"));
    }

    private sealed class DeferredRefreshScope(CrystallizeListHandler owner) : IDisposable {
        public void Dispose() => owner.FlushDeferredRefresh();
    }

    // HandleCrystallizeCallback case 18 / category switch LABEL_109
    private unsafe void KickNativeRepopulate(MiragePrismPrismBoxData* data) {
        ClearCrystallizeSelection(data);
        MemoryHelper.WriteField<byte>(data, CrystallizeItemActionPending, 0);
        data->IsPopulatingList = true;
        data->IsPopulatingComplete = false;
        MemoryHelper.WriteField<ushort>(data, CrystallizePopulateIndex, 0);
        MemoryHelper.WriteField(data, CrystallizeSectionSlot, -1);
    }

    private unsafe bool PopulateCrystallizeAndFireRefreshDetour(AgentMiragePrismPrismBox* thisPtr) {
        if (IsFilteringActive && thisPtr is not null && thisPtr->Data is not null) {
            var data = thisPtr->Data;
            // only strip at the start of an AtkValues build; mid-build resumes must keep the already-filtered list
            if (MemoryHelper.ReadField<ushort>(data, CrystallizePopulateIndex) == 0 && data->CrystallizeTreeRowCount == 0)
                ApplyFiltersToCrystallizeItems(data);
        }

        return _populateHook!.Original(thisPtr);
    }

    private unsafe void ApplyFiltersToCrystallizeItems(MiragePrismPrismBoxData* data) {
        var sourceCount = data->CrystallizeItemCount;
        if (sourceCount == 0)
            return;

        ushort write = 0;
        for (ushort read = 0; read < sourceCount; read++) {
            var row = data->CrystallizeItems[read];
            if (ShouldHide(row.ItemId))
                continue;

            if (write != read)
                data->CrystallizeItems[write] = row;
            write++;
        }

        for (var i = write; i < sourceCount; i++)
            data->CrystallizeItems[i] = default;

        data->CrystallizeItemCount = write;
        ClampCrystallizeSelection(data);

        if (write != sourceCount)
            LogFilterApplied(data, sourceCount, write);
    }

    private bool ShouldHide(uint itemId) {
        var baseId = ItemUtil.GetBaseId(itemId).ItemId;
        return baseId != 0 && Filters.Any(f => f.IsEnabled && f.ShouldHide(baseId));
    }

    private static unsafe void ClearCrystallizeSelection(MiragePrismPrismBoxData* data) {
        data->CrystallizeItemIndex = 0;
        data->CrystallizeSelectedItem = default;
    }

    private static unsafe void ClampCrystallizeSelection(MiragePrismPrismBoxData* data) {
        if (data->CrystallizeItemCount == 0) {
            ClearCrystallizeSelection(data);
            return;
        }

        if (data->CrystallizeItemIndex >= data->CrystallizeItemCount)
            data->CrystallizeItemIndex = (ushort)(data->CrystallizeItemCount - 1);
    }

    private static unsafe MiragePrismPrismBoxData* GetData() {
        var agent = AgentMiragePrismPrismBox.Instance();
        return agent is null ? null : agent->Data;
    }

    public async ValueTask DisposeAsync() {
        await IFramework.Get().Run(() => {
            IGameInventory.Get().InventoryChanged -= OnInventoryChanged;
            _populateHook?.Dispose();
        });
    }
}
