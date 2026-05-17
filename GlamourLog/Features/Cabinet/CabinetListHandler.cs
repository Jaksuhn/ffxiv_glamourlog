using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Controllers;

namespace GlamourLog.Features.Cabinet;

internal sealed unsafe class CabinetListHandler : IDisposable {
    private const string _addonName = "Cabinet";
    private readonly ICabinetRowFilter[] _filters;
    private readonly AddonController<AtkUnitBase> _addonController;
    private readonly Dictionary<uint, RowDetails> _rowMetrics = [];
    private readonly Dictionary<int, bool> _hideByListIndex = [];
    private bool _needsRestore;

    public CabinetListHandler() {
        _filters = [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()];

        _addonController = new AddonController<AtkUnitBase> {
            AddonName = _addonName,
            OnPreRefresh = OnPreRefresh,
            OnFinalize = OnFinalize,
        };

        _addonController.Enable();
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, _addonName, OnCabinetPreDraw);
        Svc.Get<OwnershipService>().ArmoireOwnershipChanged += OnArmoireOwnershipChanged;
    }

    public void Dispose() {
        Svc.AddonLifecycle.UnregisterListener(OnCabinetPreDraw);
        Svc.Get<OwnershipService>().ArmoireOwnershipChanged -= OnArmoireOwnershipChanged;
        _addonController.Dispose();
        _rowMetrics.Clear();
        _hideByListIndex.Clear();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool ShouldHideItem(uint itemId)
        => itemId != 0 && _filters.Any(f => f.IsEnabled && f.ShouldHide(itemId));

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(() => {
            CabinetGearsetLookup.Invalidate();
            var wasFiltering = _hideByListIndex.Count > 0;
            _hideByListIndex.Clear();

            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("Cabinet");
            if (addon is null) {
                _rowMetrics.Clear();
                _needsRestore = false;
                return;
            }

            if (!IsFilteringActive) {
                if (wasFiltering || _needsRestore)
                    RestoreCabinetList((AddonCabinet*)addon);
                else
                    _rowMetrics.Clear();

                _needsRestore = false;
                return;
            }

            _rowMetrics.Clear();
            addon->OnRefresh(0, null);
        });
    }

    private void OnArmoireOwnershipChanged() {
        if (!IsFilteringActive)
            return;

        Svc.Framework.RunOnFrameworkThread(() => {
            _hideByListIndex.Clear();
            if (RaptureAtkUnitManager.Instance()->GetAddonByName("Cabinet") is not null and var addon)
                addon->OnRefresh(0, null);
        });
    }

    private static void OnPreRefresh(AtkUnitBase* addon) {
        CabinetGearsetLookup.Invalidate();

        var service = Svc.Get<CabinetListHandler>();
        if (!service.IsFilteringActive)
            return;

        if (addon is null)
            return;

        var cabinet = (AddonCabinet*)addon;
        var list = cabinet->ItemList;
        if (list is null)
            return;

        service._hideByListIndex.Clear();
        CabinetListLayout.RestoreAllRows(list, service._rowMetrics);
    }

    private void OnCabinetPreDraw(AddonEvent type, AddonArgs args) {
        _ = type;
        var addon = (AddonCabinet*)args.GetAddon<AtkUnitBase>();
        if (addon is null)
            return;

        if (!IsFilteringActive) {
            if (_needsRestore)
                RestoreCabinetList(addon);
            return;
        }

        _needsRestore = true;
        ApplyFilter(addon);
    }

    private void ApplyFilter(AddonCabinet* addon) {
        var agent = AgentCabinet.Instance();
        if (agent is null) return;
        var list = addon->ItemList;
        if (list is null) return;

        try {
            var rowCount = addon->ItemList->ListLength;
            _hideByListIndex.Clear();

            for (var i = 0; i < rowCount; i++) {
                var itemId = agent->ItemCaches[i].Id;
                _hideByListIndex[i] = ShouldHideItem(itemId);
            }

            var poolCount = list->AllocatedItemRendererListLength;
            for (var i = 0; i < poolCount; i++) {
                var renderer = list->GetItemRenderer(i);
                if (renderer is null)
                    continue;

                var listIndex = renderer->ListItemIndex;
                if (listIndex < 0 || listIndex >= rowCount)
                    continue;

                if (_hideByListIndex.TryGetValue(listIndex, out var shouldHide) && shouldHide)
                    CabinetListLayout.HideRow(renderer, _rowMetrics);
                else
                    CabinetListLayout.ShowRow(renderer, _rowMetrics);
            }

            CabinetListLayout.CompactVisibleRows(list, _rowMetrics, _hideByListIndex);
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, "[Cabinet] Failed to apply filter");
        }
    }

    private void OnFinalize(AtkUnitBase* addon) {
        _ = addon;
        _rowMetrics.Clear();
        _hideByListIndex.Clear();
        _needsRestore = false;
    }

    private static void RestoreCabinetList(AddonCabinet* addon) {
        if (addon is null)
            return;

        var list = addon->ItemList;
        if (list is null)
            return;

        var service = Svc.Get<CabinetListHandler>();
        CabinetListLayout.RestoreAllRows(list, service._rowMetrics);
        CabinetListLayout.RefreshListLayout(list);
        addon->OnRefresh(0, null);

        service._rowMetrics.Clear();
        service._hideByListIndex.Clear();
        service._needsRestore = false;
    }
}
