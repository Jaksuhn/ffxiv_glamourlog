using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.Cabinet;

internal struct RowDetails {
    internal float DefaultHeight;
    internal float DefaultY;
    internal bool HiddenByFilter;
}

internal static unsafe class CabinetListLayout {
    private const float FallbackRowHeight = 28f;

    internal static void HideRow(AtkComponentListItemRenderer* renderer, Dictionary<uint, RowDetails> metrics) {
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

    internal static void ShowRow(AtkComponentListItemRenderer* renderer, Dictionary<uint, RowDetails> metrics) {
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

    internal static void RestoreAllRows(AtkComponentList* list, Dictionary<uint, RowDetails> metrics) {
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
            SetSubtreeDrawn(renderer, true);

            var owner = (AtkResNode*)renderer->OwnerNode;
            owner->Y = (short)(baseY + i * rowHeight);
        }
    }

    internal static void RefreshListLayout(AtkComponentList* list) {
        list->UpdateListItems();
        list->IsScrollRefreshPending = true;
        list->IsUpdatePending = true;
    }

    internal static void CompactVisibleRows(AtkComponentList* list, Dictionary<uint, RowDetails> metrics, IReadOnlyDictionary<int, bool> hideByListIndex) {
        var count = list->AllocatedItemRendererListLength;
        if (count <= 0)
            return;

        var rowHeight = GetRowHeight(list, metrics);
        var baseY = GetBaseY(list, metrics, count);
        var viewportOffset = CountVisibleItemsBefore(list, list->FirstVisibleItemIndex, hideByListIndex);

        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            var owner = (AtkResNode*)renderer->OwnerNode;
            if (!CountsForCompactLayout(owner))
                continue;

            CaptureMetrics(owner, metrics);

            var listIndex = renderer->ListItemIndex;
            var displaySlot = CountVisibleItemsBefore(list, listIndex + 1, hideByListIndex) - 1 - viewportOffset;
            if (displaySlot < 0)
                displaySlot = 0;

            owner->Y = (short)(baseY + displaySlot * rowHeight);
        }
    }

    private static int CountVisibleItemsBefore(AtkComponentList* list, int endExclusive, IReadOnlyDictionary<int, bool> hideByListIndex) {
        if (endExclusive <= 0)
            return 0;

        var limit = Math.Min(endExclusive, list->ListLength);
        var count = 0;
        for (var i = 0; i < limit; i++) {
            if (!hideByListIndex.TryGetValue(i, out var hide) || !hide)
                count++;
        }

        return count;
    }

    private static void RestoreRow(AtkComponentListItemRenderer* renderer, Dictionary<uint, RowDetails> metrics, bool restoreY) {
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

    private static float GetRowHeight(AtkComponentList* list, Dictionary<uint, RowDetails> metrics) {
        if (list->ItemHeight > 0)
            return list->ItemHeight;

        foreach (var rowMetrics in metrics.Values) {
            if (rowMetrics.DefaultHeight > 0f)
                return rowMetrics.DefaultHeight;
        }

        return FallbackRowHeight;
    }

    private static float GetRowHeightForNode(AtkResNode* owner, Dictionary<uint, RowDetails> metrics)
        => metrics.TryGetValue(owner->NodeId, out var rowMetrics) && rowMetrics.DefaultHeight > 0f
            ? rowMetrics.DefaultHeight
            : FallbackRowHeight;

    private static float GetBaseY(AtkComponentList* list, Dictionary<uint, RowDetails> metrics, int count) {
        for (var i = 0; i < count; i++) {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
                continue;

            var owner = (AtkResNode*)renderer->OwnerNode;
            if (!CountsForCompactLayout(owner))
                continue;

            if (metrics.TryGetValue(owner->NodeId, out var rowMetrics) && rowMetrics.DefaultY != 0)
                return rowMetrics.DefaultY;

            return owner->Y;
        }

        return 0f;
    }

    private static bool CountsForCompactLayout(AtkResNode* owner)
        => owner->Height > 0;

    private static bool IsRowVisible(AtkResNode* owner)
        => owner->Height > 0 && (owner->NodeFlags & NodeFlags.Visible) != 0;

    private static void CaptureMetrics(AtkResNode* owner, Dictionary<uint, RowDetails> metrics) {
        var nodeId = owner->NodeId;
        metrics.TryGetValue(nodeId, out var existing);
        if (existing.DefaultHeight > 0f)
            return;

        metrics[nodeId] = new RowDetails {
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
