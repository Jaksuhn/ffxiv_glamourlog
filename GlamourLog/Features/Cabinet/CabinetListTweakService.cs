using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Classes;
using KamiToolKit.Controllers;

namespace GlamourLog.Features.Cabinet;

internal sealed unsafe class CabinetListTweakService : IDisposable {
    private const string _addonName = "Cabinet";
    private readonly ICabinetRowFilter[] _filters;
    private readonly AddonController<AtkUnitBase> _addonController;
    private readonly NativeListController<AtkUnitBase, CabinetListItemData> _listController;
    private readonly Dictionary<uint, CabinetRowMetrics> _rowMetrics = [];
    private readonly Dictionary<uint, bool> _rowHideCache = [];
    private bool _needsRestore;

    private class CabinetListItemData : ListItemData;

    public CabinetListTweakService() {
        _filters = [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()];

        _addonController = new AddonController<AtkUnitBase> {
            AddonName = _addonName,
            OnPreRefresh = OnPreRefresh,
            OnFinalize = OnFinalize,
        };

        _listController = new NativeListController<AtkUnitBase, CabinetListItemData> {
            AddonName = _addonName,
            GetPopulatorNode = GetPopulatorNode,
            ShouldModifyElement = (_, _) => IsFilteringActive,
            UpdateElement = OnPopulateElement,
            ResetElement = OnResetElement,
        };

        _addonController.Enable();
        _listController.Enable();
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, _addonName, OnCabinetPreDraw);
        Svc.Get<OwnershipService>().ArmoireOwnershipChanged += OnArmoireOwnershipChanged;
    }

    public void Dispose() {
        Svc.AddonLifecycle.UnregisterListener(OnCabinetPreDraw);
        Svc.Get<OwnershipService>().ArmoireOwnershipChanged -= OnArmoireOwnershipChanged;
        _listController.Dispose();
        _addonController.Dispose();
        _rowMetrics.Clear();
        _rowHideCache.Clear();
    }

    private bool IsFilteringActive => _filters.Any(f => f.IsEnabled);

    private bool ShouldHideRow(CabinetItemRenderer row)
        => _filters.Any(f => f.IsEnabled && f.ShouldHide(row));

    internal void OnConfigChanged() {
        Svc.Framework.RunOnFrameworkThread(() => {
            CabinetGearsetLookup.Invalidate();
            var wasFiltering = _rowHideCache.Count > 0 || _listController.ModifiedIndexes.Count > 0;
            _rowHideCache.Clear();

            var addon = GetCabinetAddon();
            if (addon is null) {
                _rowMetrics.Clear();
                _listController.ModifiedIndexes.Clear();
                _needsRestore = false;
                return;
            }

            if (!IsFilteringActive) {
                if (wasFiltering || _needsRestore)
                    RestoreCabinetList(addon);
                else
                    _rowMetrics.Clear();

                _listController.ModifiedIndexes.Clear();
                _needsRestore = false;
                return;
            }

            _rowMetrics.Clear();
            _listController.ModifiedIndexes.Clear();
            RequestCabinetRefresh(addon);
        });
    }

    private void OnArmoireOwnershipChanged() {
        if (!IsFilteringActive)
            return;

        Svc.Framework.RunOnFrameworkThread(() => {
            _rowHideCache.Clear();
            var addon = GetCabinetAddon();
            if (addon is null)
                return;
            RequestCabinetRefresh(addon);
        });
    }

    private static void OnPreRefresh(AtkUnitBase* addon) {
        _ = addon;
        CabinetGearsetLookup.Invalidate();
        Svc.Get<CabinetListTweakService>()._rowHideCache.Clear();
    }

    private void OnCabinetPreDraw(AddonEvent type, AddonArgs args) {
        _ = type;
        var addon = args.GetAddon<AtkUnitBase>();
        if (!IsFilteringActive) {
            if (_needsRestore)
                RestoreCabinetList(addon);
            return;
        }

        _needsRestore = true;
        ApplyCabinetFilter(addon);
    }

    private static void OnResetElement(AtkUnitBase* addon, CabinetListItemData listItem) {
        _ = addon;
        var renderer = listItem.ItemRenderer;
        if (renderer is null && listItem.ItemInfo is not null)
            renderer = listItem.ItemInfo->ListItem->Renderer;
        if (renderer is null)
            return;

        var service = Svc.Get<CabinetListTweakService>();
        CabinetListLayout.ShowRow(renderer, service._rowMetrics);
    }

    private static void OnPopulateElement(AtkUnitBase* addon, CabinetListItemData listItem) {
        _ = addon;
        var service = Svc.Get<CabinetListTweakService>();
        if (!service.IsFilteringActive)
            return;

        var renderer = listItem.ItemRenderer;
        if (renderer is null && listItem.ItemInfo is not null)
            renderer = listItem.ItemInfo->ListItem->Renderer;
        if (renderer is null)
            return;

        try {
            var nodeId = renderer->OwnerNode->NodeId;
            var shouldHide = CabinetRowResolver.TryResolve(renderer, listItem.ItemInfo, out var row)
                && service.ShouldHideRow(row);

            service._rowHideCache[nodeId] = shouldHide;

            if (shouldHide)
                CabinetListLayout.HideRow(renderer, service._rowMetrics);
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, "[Cabinet] Failed to populate");
        }
    }

    private static void ApplyCabinetFilter(AtkUnitBase* addon) {
        var list = CabinetListLayout.GetList(addon);
        if (list is null)
            return;

        try {
            var service = Svc.Get<CabinetListTweakService>();
            var count = list->AllocatedItemRendererListLength;

            for (var i = 0; i < count; i++) {
                var renderer = list->GetItemRenderer(i);
                if (renderer is null)
                    continue;

                var nodeId = renderer->OwnerNode->NodeId;
                var shouldHide = service._rowHideCache.TryGetValue(nodeId, out var cached)
                    ? cached
                    : CabinetRowResolver.TryResolve(renderer, null, out var row) && service.ShouldHideRow(row);

                if (shouldHide)
                    CabinetListLayout.HideRow(renderer, service._rowMetrics);
                else
                    CabinetListLayout.ShowRow(renderer, service._rowMetrics);
            }

            CabinetListLayout.CompactVisibleRows(list, service._rowMetrics);
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, "[Cabinet] Failed to apply filter");
        }
    }

    private static void OnFinalize(AtkUnitBase* addon) {
        _ = addon;
        var service = Svc.Get<CabinetListTweakService>();
        service._rowMetrics.Clear();
        service._rowHideCache.Clear();
        service._listController.ModifiedIndexes.Clear();
        service._needsRestore = false;
    }

    private static void RestoreCabinetList(AtkUnitBase* addon) {
        if (addon is null)
            return;

        var list = CabinetListLayout.GetList(addon);
        if (list is null)
            return;

        var service = Svc.Get<CabinetListTweakService>();
        CabinetListLayout.RestoreAllRows(list, service._rowMetrics);
        CabinetListLayout.RefreshListLayout(list);
        RequestCabinetRefresh(addon);

        service._rowMetrics.Clear();
        service._rowHideCache.Clear();
        service._needsRestore = false;
    }

    private static AtkComponentListItemRenderer* GetPopulatorNode(AtkUnitBase* addon) {
        var list = CabinetListLayout.GetList(addon);
        return list is null ? null : list->FirstAtkComponentListItemRenderer;
    }

    private static AtkUnitBase* GetCabinetAddon() {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(_addonName);
        return addon is null || !addon->IsVisible ? null : addon;
    }

    private static void RequestCabinetRefresh(AtkUnitBase* addon)
        => addon->OnRefresh(0, null);
}