using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.Cabinet;

internal struct CabinetRowMetrics {
    internal float DefaultHeight;
    internal float DefaultY;
    internal bool HiddenByFilter;
}

internal static unsafe class CabinetListLayout {
    internal const int CabinetListNodeId = 11;
    private const float FallbackRowHeight = 28f;

    internal static AtkComponentList* GetList(AtkUnitBase* addon) {
        AtkComponentNode* componentNode = null;

        var direct = addon->GetNodeById(CabinetListNodeId);
        if (direct is not null && direct->GetNodeType() == NodeType.Component)
            componentNode = (AtkComponentNode*)direct;

        if (componentNode is null) {
            var searched = addon->UldManager.SearchNodeById<AtkComponentNode>(CabinetListNodeId);
            if (searched is not null)
                componentNode = searched;
        }

        return componentNode is null || componentNode->Component is null ? null : (AtkComponentList*)componentNode->Component;
    }

    internal static void HideRow(AtkComponentListItemRenderer* renderer, Dictionary<uint, CabinetRowMetrics> metrics) {
        var owner = (AtkResNode*)renderer->OwnerNode;
        CaptureMetrics(owner, metrics);

        renderer->SetEnabledState(false);
        SetSubtreeDrawn(renderer, false);
        owner->ToggleVisibility(false);
        owner->NodeFlags &= ~NodeFlags.Visible;
        owner->Height = 0;
        SetCollisionEnabled(renderer, false, 0);

        if (metrics.TryGetValue(owner->NodeId, out var rowMetrics)) {
            rowMetrics.HiddenByFilter = true;
            metrics[owner->NodeId] = rowMetrics;
        }
    }

    internal static void ShowRow(AtkComponentListItemRenderer* renderer, Dictionary<uint, CabinetRowMetrics> metrics) {
        var owner = (AtkResNode*)renderer->OwnerNode;
        var nodeId = owner->NodeId;
        var wasHiddenByFilter = metrics.TryGetValue(nodeId, out var rowMetrics) && rowMetrics.HiddenByFilter;
        if (IsRowVisible(owner) && renderer->IsEnabled && !wasHiddenByFilter)
            return;

        RestoreRow(renderer, metrics, restoreY: true);
        if (wasHiddenByFilter)
            SetSubtreeDrawn(renderer, true);

        if (metrics.TryGetValue(nodeId, out rowMetrics)) {
            rowMetrics.HiddenByFilter = false;
            metrics[nodeId] = rowMetrics;
        }
    }

    internal static void RestoreAllRows(AtkComponentList* list, Dictionary<uint, CabinetRowMetrics> metrics) {
        var count = list->AllocatedItemRendererListLength;
        if (count <= 0)
            return;

        var rowHeight = GetRowHeight(list, metrics);
        var baseY = GetBaseY(list, metrics, count);

        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            RestoreRow(renderer, metrics, restoreY: false);

            var owner = (AtkResNode*)renderer->OwnerNode;
            owner->Y = (short)(baseY + i * rowHeight);
        }
    }

    internal static void RefreshListLayout(AtkComponentList* list) {
        list->UpdateListItems();
        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    internal static void CompactVisibleRows(AtkComponentList* list, Dictionary<uint, CabinetRowMetrics> metrics) {
        var rowHeight = GetRowHeight(list, metrics);
        var baseY = GetBaseY(list, metrics, list->AllocatedItemRendererListLength);
        var visibleIndex = 0;

        var count = list->AllocatedItemRendererListLength;
        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            var owner = (AtkResNode*)renderer->OwnerNode;
            if (!IsRowVisible(owner))
                continue;

            owner->Y = (short)(baseY + visibleIndex * rowHeight);
            visibleIndex++;
        }
    }

    private static void RestoreRow(AtkComponentListItemRenderer* renderer, Dictionary<uint, CabinetRowMetrics> metrics, bool restoreY) {
        var owner = (AtkResNode*)renderer->OwnerNode;
        CaptureMetrics(owner, metrics);

        var rowHeight = (ushort)GetRowHeightForNode(owner, metrics);
        renderer->SetEnabledState(true);
        owner->Height = rowHeight;
        owner->ToggleVisibility(true);
        owner->NodeFlags |= NodeFlags.Visible;
        SetCollisionEnabled(renderer, true, rowHeight);

        if (restoreY && metrics.TryGetValue(owner->NodeId, out var rowMetrics))
            owner->Y = (short)rowMetrics.DefaultY;
    }

    private static float GetRowHeight(AtkComponentList* list, Dictionary<uint, CabinetRowMetrics> metrics) {
        if (list->ItemHeight > 0)
            return list->ItemHeight;

        foreach (var rowMetrics in metrics.Values) {
            if (rowMetrics.DefaultHeight > 0f)
                return rowMetrics.DefaultHeight;
        }

        return FallbackRowHeight;
    }

    private static float GetRowHeightForNode(AtkResNode* owner, Dictionary<uint, CabinetRowMetrics> metrics)
        => metrics.TryGetValue(owner->NodeId, out var rowMetrics) && rowMetrics.DefaultHeight > 0f
            ? rowMetrics.DefaultHeight
            : FallbackRowHeight;

    private static float GetBaseY(AtkComponentList* list, Dictionary<uint, CabinetRowMetrics> metrics, int count) {
        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            var owner = (AtkResNode*)renderer->OwnerNode;
            if (metrics.TryGetValue(owner->NodeId, out var rowMetrics))
                return rowMetrics.DefaultY;
        }

        var first = list->GetItemRenderer(0);
        return first is null ? 0f : ((AtkResNode*)first->OwnerNode)->Y;
    }

    private static bool IsRowVisible(AtkResNode* owner)
        => owner->Height > 0 && (owner->NodeFlags & NodeFlags.Visible) != 0;

    private static void CaptureMetrics(AtkResNode* owner, Dictionary<uint, CabinetRowMetrics> metrics) {
        var nodeId = owner->NodeId;
        metrics.TryGetValue(nodeId, out var existing);
        if (existing.DefaultHeight > 0f)
            return;

        metrics[nodeId] = new CabinetRowMetrics {
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
}
