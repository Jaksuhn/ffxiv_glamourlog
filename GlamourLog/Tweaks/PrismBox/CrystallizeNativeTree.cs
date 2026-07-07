using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.PrismBox;

// hook LoadAtkValues for buffer layout, repopulate from filtered snapshot
internal sealed unsafe class CrystallizeNativeTree : IDisposable {
    internal const string AddonName = "MiragePrismPrismBoxCrystallize";
    private const uint ItemTreeListNodeId = 11;

    private readonly Hook<AtkComponentTreeList.Delegates.LoadAtkValues>? _loadAtkValuesHook;

    private AtkResNode* _treeResNode;
    private CrystallizeAtkBufferLayout _bufferLayout; // uint/string offsets captured from LoadAtkValues detour

    internal bool HasBufferLayout => _bufferLayout.IsValid;
    internal bool HasSnapshot => Snapshot.Length > 0; // clone of addon->AtkValues
    internal bool HasLayout => Layout.Length > 0;
    internal int NativeSlotCount { get; private set; }
    internal int FilteredSlotCount { get; private set; }
    internal AtkComponentTreeList* TreeList { get; private set; }
    internal CrystallizeAtkSlot[] Layout { get; private set; } = []; // parsed tree/leaf slots from Snapshot
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

    internal void EnsureVisible(AtkUnitBase* addon, bool hideOtherTreeLists = false)
        => SetTreeListVisible(addon, true, hideOtherTreeLists);

    private void SetTreeListVisible(AtkUnitBase* addon, bool visible, bool hideOtherTreeLists) {
        Resolve(addon);
        foreach (var nodePtr in addon->UldManager.Nodes) {
            var node = nodePtr.Value;
            var tree = node is not null ? node->GetAsAtkComponentTreeList() : null;
            if (tree is null)
                continue;

            var isFilteredTree = _treeResNode is not null && (node == _treeResNode || node->IsDuplicatedNode());
            if (!isFilteredTree) {
                if (hideOtherTreeLists)
                    SetNodeVisible(node, false);
                continue;
            }

            var show = visible;
            SetNodeVisible(node, show);
            SetTreeScrollBarVisible(tree, show);
            ((AtkComponentList*)tree)->IsItemInteractionEnabled = show;
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
            ? CrystallizeListAtk.InferBoundedItemCount(Snapshot, inferred, categoryRowCount, includeHeaders, _bufferLayout) // clip stale slots from prior tab
            : inferred;
        Layout = CrystallizeListAtk.Parse(Snapshot, NativeSlotCount, _bufferLayout, categoryRowCount);
    }

    internal void RepopulateFiltered(AtkUnitBase* addon, bool isFilteringActive, int displayCount, HashSet<int> visibleSources, Func<int, bool> shouldHideLeaf, Func<int, bool> shouldExcludeSource) {
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
            if (isFilteringActive && displayCount > 0 && NativeSlotCount >= displayCount)
                return; // apply failed but layout still big enough — keep last tree state

            var clearedAtk = CrystallizeListAtk.Clone(Snapshot);
            var inferred = CrystallizeListAtk.InferItemCount(Snapshot, _bufferLayout);
            var clearThrough = Math.Max(inferred, NativeSlotCount);
            if (clearThrough <= 0)
                clearThrough = 1;
            CrystallizeListAtk.ClearSlots(clearedAtk, 0, clearThrough, _bufferLayout);
            LoadTreeFromAtkSnapshot(tree, clearedAtk, 0);
            if (addon is not null && addon->AtkValues is not null)
                CrystallizeListAtk.WriteSlotsToAtkBuffer(clearedAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, 0, clearThrough);
            Snapshot = CrystallizeListAtk.Clone(clearedAtk);
            for (var slot = 0; slot < tree->Items.Count; slot++)
                CrystallizeListAtk.ClearTreeItemDisplay(tree->GetItem(slot));
            ((AtkComponentList*)tree)->SetItemCount(0);
            ResetTreeScroll(tree);
            RefreshTreeListLayout(tree);
            return;
        }

        LoadTreeFromAtkSnapshot(tree, workingAtk, FilteredSlotCount);
        var trailingInferred = CrystallizeListAtk.InferItemCount(Snapshot, _bufferLayout);
        var trailingClearThrough = Math.Max(trailingInferred, NativeSlotCount);
        if (trailingClearThrough > FilteredSlotCount) {
            CrystallizeListAtk.ClearSlots(workingAtk, FilteredSlotCount, trailingClearThrough, _bufferLayout); // zero tail slots after compacted visible rows
            if (addon is not null && addon->AtkValues is not null)
                CrystallizeListAtk.WriteSlotsToAtkBuffer(workingAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, FilteredSlotCount, trailingClearThrough);
        }
        if (addon is not null && addon->AtkValues is not null)
            CrystallizeListAtk.WriteSlotsToAtkBuffer(workingAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, 0, FilteredSlotCount);
        Snapshot = CrystallizeListAtk.Clone(workingAtk); // keep Snapshot aligned with what tree now shows
        ResetTreeScroll(tree);
        RefreshTreeListLayout(tree);
    }

    public void Dispose() => _loadAtkValuesHook?.Dispose();

    private void LoadAtkValuesDetour(AtkComponentTreeList* thisPtr, int atkValuesCount, AtkValue* atkValues, int uintValuesOffset, int stringValuesOffset, int uintValuesCountPerItem, int stringValuesCountPerItem, int itemCount, ListComponentCallBackInterface* callBackInterface) {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(AddonName);
        var isTargetTree = addon is not null && IsTargetTree(thisPtr, addon);
        if (itemCount > 0 && isTargetTree)
            TryCaptureBufferLayout(thisPtr, uintValuesOffset, stringValuesOffset, uintValuesCountPerItem, stringValuesCountPerItem, atkValuesCount);

        _loadAtkValuesHook!.Original(thisPtr, atkValuesCount, atkValues, uintValuesOffset, stringValuesOffset, uintValuesCountPerItem, stringValuesCountPerItem, itemCount, callBackInterface);
    }

    private static bool IsTargetTree(AtkComponentTreeList* tree, AtkUnitBase* addon) {
        var node = addon->GetNodeById(ItemTreeListNodeId);
        return node is not null && node->GetAsAtkComponentTreeList() == tree;
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
            return false; // ignore LoadAtkValues from other tree lists on same addon

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
                    CrystallizeListAtk.CopySlotToTreeItem(atkSnapshot, slot, item, _bufferLayout); // LoadAtkValues doesn't always fill renderer uint/string arrays
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
        if (node is null)
            return;
        node->ToggleVisibility(visible);
        if (visible)
            node->NodeFlags |= NodeFlags.Visible;
        else
            node->NodeFlags &= ~NodeFlags.Visible;
    }

    private static void SetTreeScrollBarVisible(AtkComponentTreeList* tree, bool visible) {
        if (tree is null)
            return;
        var scrollBar = ((AtkComponentList*)tree)->ScrollBarComponent;
        if (scrollBar is not null)
            SetNodeVisible((AtkResNode*)scrollBar->OwnerNode, visible);
    }
}
