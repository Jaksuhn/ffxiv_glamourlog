using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.PrismBox;

internal sealed unsafe class CrystallizeNativeTree : IDisposable {
    internal const string AddonName = "MiragePrismPrismBoxCrystallize";
    private const uint ItemTreeListNodeId = 11;

    private readonly Hook<AtkComponentTreeList.Delegates.LoadAtkValues>? _loadAtkValuesHook;

    private AtkResNode* _treeResNode;
    private CrystallizeAtkBufferLayout _bufferLayout;

    internal bool HasBufferLayout => _bufferLayout.IsValid;
    internal bool HasSnapshot => Snapshot.Length > 0;
    internal bool HasLayout => Layout.Length > 0;
    internal int NativeSlotCount { get; private set; }
    internal int FilteredSlotCount { get; private set; }
    internal AtkComponentTreeList* TreeList { get; private set; }
    internal CrystallizeAtkSlot[] Layout { get; private set; } = [];
    internal AtkValue[] Snapshot { get; private set; } = [];

    internal CrystallizeNativeTree() {
        _loadAtkValuesHook = Svc.Hook.HookFromAddress<AtkComponentTreeList.Delegates.LoadAtkValues>((nint)AtkComponentTreeList.MemberFunctionPointers.LoadAtkValues, LoadAtkValuesDetour);
        _loadAtkValuesHook.Enable();
    }

    internal void InvalidateTreeCache() {
        _treeResNode = null;
        TreeList = null;
    }

    internal void InvalidateAtkCache() {
        Snapshot = [];
        Layout = [];
        NativeSlotCount = 0;
        FilteredSlotCount = 0;
    }

    internal void InvalidateAll() {
        InvalidateTreeCache();
        InvalidateAtkCache();
    }

    internal void Resolve(AtkUnitBase* addon) {
        var nativeNode = addon->GetNodeById(ItemTreeListNodeId);
        if (nativeNode is null)
            return;
        _treeResNode = nativeNode;
        TreeList = nativeNode->GetAsAtkComponentTreeList();
    }

    internal void EnsureVisible(AtkUnitBase* addon) {
        Resolve(addon);
        foreach (var nodePtr in addon->UldManager.Nodes) {
            var node = nodePtr.Value;
            var tree = node is not null ? node->GetAsAtkComponentTreeList() : null;
            if (tree is null)
                continue;
            if (node != _treeResNode && !node->IsDuplicatedNode())
                continue;
            var visible = _treeResNode is not null && node == _treeResNode;
            SetNodeVisible(node, visible);
            SetTreeScrollBarVisible(tree, visible);
            ((AtkComponentList*)tree)->IsItemInteractionEnabled = visible;
        }
        addon->UldManager.UpdateDrawNodeList();
    }

    internal void CaptureAtkSnapshot(AtkUnitBase* addon) {
        if (addon->AtkValues is null || addon->AtkValuesCount <= 0) {
            Snapshot = [];
            return;
        }
        var copy = new AtkValue[addon->AtkValuesCount];
        for (var i = 0; i < addon->AtkValuesCount; i++)
            copy[i] = addon->AtkValues[i];
        Snapshot = copy;
    }

    internal void ParseLayout(int categoryRowCount, bool force = false) {
        if (!HasBufferLayout || Snapshot.Length == 0) {
            Layout = [];
            NativeSlotCount = 0;
            return;
        }
        if (!force && Layout.Length > 0)
            return;

        var inferred = CrystallizeListAtk.InferItemCount(Snapshot, _bufferLayout);
        var includeHeaders = categoryRowCount > 0;
        NativeSlotCount = categoryRowCount > 0
            ? CrystallizeListAtk.InferBoundedItemCount(Snapshot, inferred, categoryRowCount, includeHeaders, _bufferLayout)
            : inferred;
        Layout = CrystallizeListAtk.Parse(Snapshot, NativeSlotCount, _bufferLayout, categoryRowCount);
    }

    internal int GetLayoutMaxSourceIndex() {
        var maxSource = -1;
        for (var i = 0; i < Layout.Length; i++) {
            if (!Layout[i].IsLeaf)
                continue;
            if (Layout[i].SourceIndex > maxSource)
                maxSource = Layout[i].SourceIndex;
        }
        return maxSource >= 0 ? maxSource + 1 : 0;
    }

    internal int ListLength => TreeList is not null ? ((AtkComponentList*)TreeList)->ListLength : 0;

    internal bool IsTruncatedVersus(int categoryRowCount, int agentCount)
        => categoryRowCount > 0
           && ListLength > 0
           && agentCount >= categoryRowCount
           && ListLength < agentCount;

    internal void FillMissingRows(PrismBoxCrystallizeItem[] rows, MiragePrismPrismBoxData* data) {
        for (var slot = 0; slot < Layout.Length; slot++) {
            ref readonly var entry = ref Layout[slot];
            if (!entry.IsLeaf)
                continue;
            var sourceIndex = entry.SourceIndex;
            if (sourceIndex < 0 || sourceIndex >= rows.Length)
                continue;
            if (rows[sourceIndex].ItemId != 0)
                continue;
            if (data->CrystallizeItems[sourceIndex].ItemId != 0) {
                rows[sourceIndex] = data->CrystallizeItems[sourceIndex];
                continue;
            }
            if (CrystallizeListAtk.TryReadCategoryRow(Snapshot, slot, entry, _bufferLayout, out var row))
                rows[sourceIndex] = row;
            else if (TreeList is not null
                     && CrystallizeListAtk.TryReadCategoryRowFromTreeItem(TreeList->GetItem(slot), entry, out row))
                rows[sourceIndex] = row;
        }
    }

    internal void RepopulateFiltered(bool isFilteringActive, int displayCount, HashSet<int> visibleSources, Func<int, bool> shouldHideLeaf, Func<int, bool> shouldExcludeSource) {
        var tree = TreeList;
        if (tree is null || !HasBufferLayout)
            return;
        if (Snapshot.Length == 0 || Layout.Length == 0) {
            ClearTreeDisplay(tree);
            return;
        }

        var workingAtk = CrystallizeListAtk.Clone(Snapshot);
        FilteredSlotCount = 0;
        if (isFilteringActive) {
            FilteredSlotCount = CrystallizeListAtk.ApplyToBuffer(workingAtk, Layout, shouldHideLeaf, _bufferLayout, NativeSlotCount, displayCount > 0, visibleSources, shouldExcludeSource);
        }

        if (FilteredSlotCount <= 0) {
            var clearedAtk = CrystallizeListAtk.Clone(Snapshot);
            var clearThrough = Math.Max(NativeSlotCount, 1);
            CrystallizeListAtk.ClearSlots(clearedAtk, 0, clearThrough, _bufferLayout);
            LoadTreeFromAtkSnapshot(tree, clearedAtk, 0);
            ResetTreeScroll(tree);
            return;
        }

        LoadTreeFromAtkSnapshot(tree, workingAtk, FilteredSlotCount);
        ResetTreeScroll(tree);
        RefreshTreeListLayout(tree);
    }

    public void Dispose() => _loadAtkValuesHook?.Dispose();

    private void LoadAtkValuesDetour(
        AtkComponentTreeList* thisPtr,
        int atkValuesCount,
        AtkValue* atkValues,
        int uintValuesOffset,
        int stringValuesOffset,
        int uintValuesCountPerItem,
        int stringValuesCountPerItem,
        int itemCount,
        ListComponentCallBackInterface* callBackInterface) {
        if (itemCount > 0)
            TryCaptureBufferLayout(thisPtr, uintValuesOffset, stringValuesOffset, uintValuesCountPerItem, stringValuesCountPerItem, atkValuesCount);

        _loadAtkValuesHook!.Original(thisPtr, atkValuesCount, atkValues, uintValuesOffset, stringValuesOffset, uintValuesCountPerItem, stringValuesCountPerItem, itemCount, callBackInterface);
    }

    private bool TryCaptureBufferLayout(AtkComponentTreeList* tree, int uintValuesOffset, int stringValuesOffset, int uintValuesPerItem, int stringValuesPerItem, int atkValuesCount) {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName);
        if (addon is null)
            return false;

        var node = addon->GetNodeById(ItemTreeListNodeId);
        if (node is null)
            return false;

        var nativeTree = node->GetAsAtkComponentTreeList();
        if (nativeTree != tree)
            return false;

        var candidate = new CrystallizeAtkBufferLayout {
            UintValuesOffset = uintValuesOffset,
            StringValuesOffset = stringValuesOffset,
            UintValuesPerItem = uintValuesPerItem,
            StringValuesPerItem = stringValuesPerItem,
        };
        if (!candidate.IsValid)
            return false;

        if (atkValuesCount > 0 && stringValuesOffset + stringValuesPerItem > atkValuesCount)
            return false;

        if (_bufferLayout.Matches(candidate))
            return true;

        _bufferLayout = candidate;
        return true;
    }

    private void LoadTreeFromAtkSnapshot(AtkComponentTreeList* tree, AtkValue[] atkSnapshot, int slotCount) {
        var list = (AtkComponentList*)tree;
        var callback = list->CallBackInterface;
        if (callback is null && TreeList is not null)
            callback = ((AtkComponentList*)TreeList)->CallBackInterface;
        if (callback is null || !_bufferLayout.IsValid)
            return;

        fixed (AtkValue* snapshotPtr = atkSnapshot) {
            tree->LoadAtkValues(atkSnapshot.Length, snapshotPtr, _bufferLayout.UintValuesOffset, _bufferLayout.StringValuesOffset, _bufferLayout.UintValuesPerItem, _bufferLayout.StringValuesPerItem, slotCount, callback);
        }

        if (slotCount > 0) {
            var itemCount = Math.Min(slotCount, tree->Items.Count);
            for (var slot = 0; slot < itemCount; slot++) {
                var item = tree->GetItem(slot);
                if (item is not null)
                    CrystallizeListAtk.CopySlotToTreeItem(atkSnapshot, slot, item, _bufferLayout);
            }
        }

        RefreshTreeListLayout(tree);
    }

    private static void ClearTreeDisplay(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        var list = (AtkComponentList*)tree;
        list->SetItemCount(0);
        ResetTreeScroll(tree);
        RefreshTreeListLayout(tree);
    }

    private static void ResetTreeScroll(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        var list = (AtkComponentList*)tree;
        list->FirstVisibleItemIndex = 0;
        list->PendingFirstVisibleItemIndex = 0;
        list->ScrollOffset = 0;
        list->ScrollToItem(0);
        var scrollBar = list->ScrollBarComponent;
        if (scrollBar is not null)
            scrollBar->SetScrollPosition(0);
    }

    private static void RefreshTreeListLayout(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        var list = (AtkComponentList*)tree;
        list->UpdateListItems();
        list->RecalculateVisibleItems(true);
        tree->LayoutRefreshPending = true;
        list->IsUpdatePending = true;
        list->IsScrollRefreshPending = true;
    }

    private static void SetNodeVisible(AtkResNode* node, bool visible) {
        if (node is not null)
            node->ToggleVisibility(visible);
    }

    private static void SetTreeScrollBarVisible(AtkComponentTreeList* tree, bool visible) {
        if (tree is null)
            return;
        var scrollBar = ((AtkComponentList*)tree)->ScrollBarComponent;
        if (scrollBar is not null)
            SetNodeVisible((AtkResNode*)scrollBar->OwnerNode, visible);
    }
}
