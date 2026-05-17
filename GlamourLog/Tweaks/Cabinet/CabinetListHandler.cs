using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Controllers;

namespace GlamourLog.Features.Cabinet;

internal sealed unsafe class CabinetListHandler : IDisposable {
    private const string AddonName = "Cabinet";
    private const float FallbackRowHeight = 28f;

    private readonly ICabinetRowFilter[] _filters;
    private readonly AddonController<AddonCabinet> _addonController;
    private readonly Dictionary<uint, RowDetails> _rowMetrics = [];
    private readonly Dictionary<int, bool> _hideByListIndex = [];
    private bool _needsRestore;

    public CabinetListHandler() {
        _filters = [new HideDepositedItemsFilter(), new HideGearsetItemsFilter()];

        _addonController = new AddonController<AddonCabinet> {
            AddonName = AddonName,
            OnPreRefresh = OnPreRefresh,
            OnFinalize = OnFinalize,
        };

        _addonController.Enable();
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnCabinetPreDraw);
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

            var addon = GetCabinetAddon();
            if (addon is null) {
                _rowMetrics.Clear();
                _needsRestore = false;
                return;
            }

            if (!IsFilteringActive) {
                if (wasFiltering || _needsRestore)
                    RestoreCabinetList(addon);
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
            var addon = GetCabinetAddon();
            if (addon is not null)
                addon->OnRefresh(0, null);
        });
    }

    private void OnPreRefresh(AddonCabinet* addon) {
        CabinetGearsetLookup.Invalidate();

        if (!IsFilteringActive || addon is null)
            return;

        var list = addon->ItemList;
        if (list is null)
            return;

        _hideByListIndex.Clear();
        RestoreAllRows(list);
    }

    private void OnCabinetPreDraw(AddonEvent _, AddonArgs args) {
        var addon = args.GetAddon<AddonCabinet>();
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
        var list = addon->ItemList;
        if (agent is null || list is null)
            return;

        try {
            var rowCount = list->ListLength;
            _hideByListIndex.Clear();

            for (var i = 0; i < rowCount; i++)
                _hideByListIndex[i] = ShouldHideItem(agent->ItemCaches[i].Id);

            for (var i = 0; i < list->AllocatedItemRendererListLength; i++) {
                var renderer = list->GetItemRenderer(i);
                if (renderer is null)
                    continue;

                var listIndex = renderer->ListItemIndex;
                if (listIndex < 0 || listIndex >= rowCount)
                    continue;

                if (_hideByListIndex.TryGetValue(listIndex, out var shouldHide) && shouldHide)
                    HideRow(renderer);
                else
                    ShowRow(renderer);
            }

            CompactVisibleRows(list);
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, "[Cabinet] Failed to apply filter");
        }
    }

    private void OnFinalize(AddonCabinet* _) {
        _rowMetrics.Clear();
        _hideByListIndex.Clear();
        _needsRestore = false;
    }

    private void RestoreCabinetList(AddonCabinet* addon) {
        var list = addon->ItemList;
        if (list is null)
            return;

        RestoreAllRows(list);
        RefreshListLayout(list);
        addon->OnRefresh(0, null);

        _rowMetrics.Clear();
        _hideByListIndex.Clear();
        _needsRestore = false;
    }

    private AddonCabinet* GetCabinetAddon()
        => (AddonCabinet*)RaptureAtkUnitManager.Instance()->GetAddonByName(AddonName);

    private void HideRow(AtkComponentListItemRenderer* renderer) {
        var owner = (AtkResNode*)renderer->OwnerNode;
        CaptureMetrics(owner);

        renderer->SetEnabledState(false);
        SetSubtreeDrawn(renderer, false);
        owner->ToggleVisibility(false);
        owner->NodeFlags &= ~NodeFlags.Visible;
        owner->Height = 0;
        SetCollisionEnabled(renderer, false, 0);

        if (_rowMetrics.TryGetValue(owner->NodeId, out var rowMetrics)) {
            rowMetrics.HiddenByFilter = true;
            _rowMetrics[owner->NodeId] = rowMetrics;
        }
    }

    private void ShowRow(AtkComponentListItemRenderer* renderer) {
        var owner = (AtkResNode*)renderer->OwnerNode;
        var nodeId = owner->NodeId;
        var wasHiddenByFilter = _rowMetrics.TryGetValue(nodeId, out var rowMetrics) && rowMetrics.HiddenByFilter;
        if (IsRowVisible(owner) && renderer->IsEnabled && !wasHiddenByFilter)
            return;

        RestoreRow(renderer, restoreY: true);
        if (wasHiddenByFilter)
            SetSubtreeDrawn(renderer, true);

        if (_rowMetrics.TryGetValue(nodeId, out rowMetrics)) {
            rowMetrics.HiddenByFilter = false;
            _rowMetrics[nodeId] = rowMetrics;
        }
    }

    private void RestoreAllRows(AtkComponentList* list) {
        var count = list->AllocatedItemRendererListLength;
        if (count <= 0)
            return;

        var rowHeight = GetRowHeight(list);
        var baseY = GetBaseY(list, count);

        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            RestoreRow(renderer, restoreY: false);
            SetSubtreeDrawn(renderer, true);

            var owner = (AtkResNode*)renderer->OwnerNode;
            owner->Y = (short)(baseY + i * rowHeight);
        }
    }

    private void RefreshListLayout(AtkComponentList* list) {
        list->UpdateListItems();
        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    private void CompactVisibleRows(AtkComponentList* list) {
        var count = list->AllocatedItemRendererListLength;
        if (count <= 0)
            return;

        var rowHeight = GetRowHeight(list);
        var baseY = GetBaseY(list, count);
        var viewportOffset = CountVisibleItemsBefore(list, list->FirstVisibleItemIndex);

        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            var owner = (AtkResNode*)renderer->OwnerNode;
            if (!CountsForCompactLayout(owner))
                continue;

            CaptureMetrics(owner);

            var listIndex = renderer->ListItemIndex;
            var displaySlot = CountVisibleItemsBefore(list, listIndex + 1) - 1 - viewportOffset;
            if (displaySlot < 0)
                displaySlot = 0;

            owner->Y = (short)(baseY + displaySlot * rowHeight);
        }
    }

    private int CountVisibleItemsBefore(AtkComponentList* list, int endExclusive) {
        if (endExclusive <= 0)
            return 0;

        var limit = Math.Min(endExclusive, list->ListLength);
        var count = 0;
        for (var i = 0; i < limit; i++) {
            if (!_hideByListIndex.TryGetValue(i, out var hide) || !hide)
                count++;
        }

        return count;
    }

    private void RestoreRow(AtkComponentListItemRenderer* renderer, bool restoreY) {
        var owner = (AtkResNode*)renderer->OwnerNode;
        CaptureMetrics(owner);

        var rowHeight = (ushort)GetRowHeightForNode(owner);
        renderer->SetEnabledState(true);
        owner->Height = rowHeight;
        owner->ToggleVisibility(true);
        owner->NodeFlags |= NodeFlags.Visible;
        SetCollisionEnabled(renderer, true, rowHeight);

        if (restoreY && _rowMetrics.TryGetValue(owner->NodeId, out var rowMetrics))
            owner->Y = (short)rowMetrics.DefaultY;
    }

    private float GetRowHeight(AtkComponentList* list) {
        if (list->ItemHeight > 0)
            return list->ItemHeight;

        foreach (var rowMetrics in _rowMetrics.Values) {
            if (rowMetrics.DefaultHeight > 0f)
                return rowMetrics.DefaultHeight;
        }

        return FallbackRowHeight;
    }

    private float GetRowHeightForNode(AtkResNode* owner)
        => _rowMetrics.TryGetValue(owner->NodeId, out var rowMetrics) && rowMetrics.DefaultHeight > 0f
            ? rowMetrics.DefaultHeight
            : FallbackRowHeight;

    private float GetBaseY(AtkComponentList* list, int count) {
        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            var owner = (AtkResNode*)renderer->OwnerNode;
            if (!CountsForCompactLayout(owner))
                continue;

            if (_rowMetrics.TryGetValue(owner->NodeId, out var rowMetrics) && rowMetrics.DefaultY != 0)
                return rowMetrics.DefaultY;

            return owner->Y;
        }

        return 0f;
    }

    private static bool CountsForCompactLayout(AtkResNode* owner)
        => owner->Height > 0;

    private static bool IsRowVisible(AtkResNode* owner)
        => owner->Height > 0 && (owner->NodeFlags & NodeFlags.Visible) != 0;

    private void CaptureMetrics(AtkResNode* owner) {
        var nodeId = owner->NodeId;
        _rowMetrics.TryGetValue(nodeId, out var existing);
        if (existing.DefaultHeight > 0f)
            return;

        _rowMetrics[nodeId] = new RowDetails {
            DefaultHeight = owner->Height > 0 ? owner->Height : FallbackRowHeight,
            DefaultY = owner->Y,
            HiddenByFilter = existing.HiddenByFilter,
        };
    }

    private static void SetSubtreeDrawn(AtkComponentListItemRenderer* renderer, bool drawn) {
        var uld = &renderer->UldManager;
        if (uld->NodeList is null)
            return;

        for (var i = 0u; i < uld->NodeListCount; i++) {
            var node = uld->NodeList[i];
            if (node is null)
                continue;

            if (drawn)
                node->NodeFlags |= NodeFlags.Visible;
            else
                node->NodeFlags &= ~NodeFlags.Visible;
        }
    }

    private static void SetCollisionEnabled(AtkComponentListItemRenderer* renderer, bool enabled, ushort height) {
        for (uint nodeId = 1; nodeId <= 32; nodeId++) {
            var collision = renderer->GetCollisionNodeById(nodeId);
            if (collision is null)
                continue;

            if (enabled) {
                collision->NodeFlags |= NodeFlags.Visible;
                collision->Height = height;
            }
            else {
                collision->NodeFlags &= ~NodeFlags.Visible;
                collision->Height = 0;
            }
        }
    }

    private struct RowDetails {
        internal float DefaultHeight;
        internal float DefaultY;
        internal bool HiddenByFilter;
    }
}
