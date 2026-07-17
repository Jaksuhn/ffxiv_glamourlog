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
    internal bool HasBaseline => _baselineSnapshot.Length > 0 && _baselineLayout.Length > 0;
    internal int NativeSlotCount { get; private set; }
    internal int FilteredSlotCount { get; private set; }

    private AtkValue[] _baselineSnapshot = [];
    private CrystallizeAtkSlot[] _baselineLayout = [];
    private int _baselineNativeSlotCount;
    private int _baselineCategoryRowCount;
    private int _baselineCategoryId = int.MinValue;
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

    internal void InvalidateBaseline() {
        _baselineSnapshot = [];
        _baselineLayout = [];
        _baselineNativeSlotCount = 0;
        _baselineCategoryRowCount = 0;
        _baselineCategoryId = int.MinValue;
    }

    internal bool IsBaselineValidFor(int categoryId, int categoryRowCount)
        => HasBaseline && _baselineCategoryId == categoryId && _baselineCategoryRowCount == categoryRowCount;

    internal void InvalidateAll() {
        InvalidateTreeCache();
        InvalidateAtkCache();
        InvalidateBaseline();
    }

    internal bool TryCommitBaseline(int categoryId, int categoryRowCount) {
        if (!HasBufferLayout || Snapshot.Length == 0 || Layout.Length == 0 || NativeSlotCount <= 0)
            return false;

        if (categoryRowCount > 0) {
            var inferred = CrystallizeListAtk.InferItemCount(Snapshot, _bufferLayout);
            if (inferred <= 0)
                return false;
        }

        _baselineSnapshot = CrystallizeListAtk.Clone(Snapshot);
        _baselineLayout = [.. Layout];
        _baselineNativeSlotCount = NativeSlotCount;
        _baselineCategoryRowCount = categoryRowCount;
        _baselineCategoryId = categoryId;
        return true;
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

    // agent-empty category revisit: prior tab's LoadAtkValues buffer is still present — wipe it without a baseline
    internal void ClearToEmpty(AtkUnitBase* addon) {
        Resolve(addon);
        if (TreeList is null)
            return;

        if (HasBufferLayout && addon is not null) {
            CaptureAtkSnapshot(addon);
            if (Snapshot.Length > 0) {
                var inferred = CrystallizeListAtk.InferItemCount(Snapshot, _bufferLayout);
                var clearThrough = Math.Max(inferred, NativeSlotCount);
                if (clearThrough <= 0)
                    clearThrough = 1;

                var clearedAtk = CrystallizeListAtk.Clone(Snapshot);
                CrystallizeListAtk.ClearSlots(clearedAtk, 0, clearThrough, _bufferLayout);
                if (addon->AtkValues is not null)
                    CrystallizeListAtk.WriteSlotsToAtkBuffer(clearedAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, 0, clearThrough);
                Snapshot = clearedAtk;
            }

            NativeSlotCount = 0;
            FilteredSlotCount = 0;
            Layout = [];
        }

        var list = (AtkComponentList*)TreeList;
        list->SetItemCount(0);
        for (var slot = 0; slot < TreeList->Items.Count; slot++) {
            var item = TreeList->GetItem(slot);
            if (item is null)
                continue;
            item->IsHidden = true;
            CrystallizeListAtk.ClearTreeItemDisplay(item);
        }

        HideAllItemRenderers(TreeList);
        ClearTreeDisplay(TreeList);
        if (addon is not null)
            SetTreeListVisible(addon, false, hideOtherTreeLists: true);
    }

    internal void EnsureAllTreeListsVisible(AtkUnitBase* addon) {
        if (addon is null)
            return;

        foreach (var nodePtr in addon->UldManager.Nodes) {
            var node = nodePtr.Value;
            var tree = node is not null ? node->GetAsAtkComponentTreeList() : null;
            if (tree is null)
                continue;

            SetNodeVisible(node, true);
            SetTreeScrollBarVisible(tree, true);
            ((AtkComponentList*)tree)->IsItemInteractionEnabled = true;
        }

        addon->UldManager.UpdateDrawNodeList();
    }

    private void SetTreeListVisible(AtkUnitBase* addon, bool visible, bool hideOtherTreeLists) {
        Resolve(addon);
        foreach (var nodePtr in addon->UldManager.Nodes) {
            var node = nodePtr.Value;
            var tree = node is not null ? node->GetAsAtkComponentTreeList() : null;
            if (tree is null)
                continue;

            var isFilteredTree = _treeResNode is not null && (node == _treeResNode || node->IsDuplicatedNode());
            if (!isFilteredTree) {
                if (hideOtherTreeLists) {
                    SetNodeVisible(node, false);
                    SetTreeScrollBarVisible(tree, false);
                    ((AtkComponentList*)tree)->IsItemInteractionEnabled = false;
                }
                else {
                    SetNodeVisible(node, true);
                    SetTreeScrollBarVisible(tree, true);
                    ((AtkComponentList*)tree)->IsItemInteractionEnabled = true;
                }
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

    internal void TrimCapturedSnapshot(int categoryRowCount) {
        if (!HasBufferLayout || Snapshot.Length == 0)
            return;

        var inferred = CrystallizeListAtk.InferItemCount(Snapshot, _bufferLayout);
        if (inferred <= 0)
            return;

        var bounded = CrystallizeListAtk.InferBoundedItemCount(Snapshot, inferred, categoryRowCount, categoryRowCount > 0, _bufferLayout);
        if (bounded < inferred)
            CrystallizeListAtk.ClearSlots(Snapshot, bounded, inferred, _bufferLayout);
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

        var sourceSnapshot = HasBaseline ? _baselineSnapshot : Snapshot;
        var sourceLayout = HasBaseline ? _baselineLayout : Layout;
        var sourceNativeSlotCount = HasBaseline ? _baselineNativeSlotCount : NativeSlotCount;

        if (sourceSnapshot.Length == 0 || sourceLayout.Length == 0) {
            ClearTreeDisplay(tree);
            return;
        }

        var workingAtk = CrystallizeListAtk.Clone(sourceSnapshot);
        FilteredSlotCount = 0;
        if (!isFilteringActive) {
            var inferred = CrystallizeListAtk.InferItemCount(workingAtk, _bufferLayout);
            if (inferred <= 0) {
                FinalizeTreeSlotCount(tree, 0);
                return;
            }

            var fullSlotCount = sourceNativeSlotCount > 0 ? sourceNativeSlotCount : inferred;
            if (fullSlotCount > inferred)
                fullSlotCount = inferred;
            LoadTreeFromAtkSnapshot(tree, workingAtk, fullSlotCount);
            if (addon is not null && addon->AtkValues is not null)
                CrystallizeListAtk.WriteSlotsToAtkBuffer(workingAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, 0, fullSlotCount);
            Snapshot = CrystallizeListAtk.Clone(workingAtk);
            FinalizeTreeSlotCount(tree, fullSlotCount);
            if (addon is not null)
                SetTreeListVisible(addon, true, hideOtherTreeLists: false);
            ResetTreeScroll(tree);
            RefreshTreeListLayout(tree);
            return;
        }

        if (isFilteringActive) {
            FilteredSlotCount = CrystallizeListAtk.ApplyToBuffer(workingAtk, sourceLayout, shouldHideLeaf, _bufferLayout, sourceNativeSlotCount, displayCount > 0, visibleSources, shouldExcludeSource);
        }

        if (FilteredSlotCount <= 0) {
            var clearedAtk = CrystallizeListAtk.Clone(sourceSnapshot);
            var inferred = CrystallizeListAtk.InferItemCount(sourceSnapshot, _bufferLayout);
            var clearThrough = Math.Max(inferred, sourceNativeSlotCount);
            if (clearThrough <= 0)
                clearThrough = 1;
            CrystallizeListAtk.ClearSlots(clearedAtk, 0, clearThrough, _bufferLayout);

            if (addon is not null && addon->AtkValues is not null)
                CrystallizeListAtk.WriteSlotsToAtkBuffer(clearedAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, 0, clearThrough);
            Snapshot = CrystallizeListAtk.Clone(clearedAtk);

            var list = (AtkComponentList*)tree;
            list->SetItemCount(0);
            for (var slot = 0; slot < tree->Items.Count; slot++) {
                var item = tree->GetItem(slot);
                if (item is null)
                    continue;
                item->IsHidden = true;
                CrystallizeListAtk.ClearTreeItemDisplay(item);
            }

            HideAllItemRenderers(tree);
            FilteredSlotCount = 0;
            if (addon is not null)
                SetTreeListVisible(addon, false, hideOtherTreeLists: true);
            ResetTreeScroll(tree);
            RefreshTreeListLayout(tree);
            return;
        }

        if (addon is not null)
            SetTreeListVisible(addon, true, hideOtherTreeLists: false);

        LoadTreeFromAtkSnapshot(tree, workingAtk, FilteredSlotCount);
        var trailingInferred = CrystallizeListAtk.InferItemCount(sourceSnapshot, _bufferLayout);
        var trailingClearThrough = Math.Max(trailingInferred, sourceNativeSlotCount);
        if (trailingClearThrough > FilteredSlotCount) {
            CrystallizeListAtk.ClearSlots(workingAtk, FilteredSlotCount, trailingClearThrough, _bufferLayout); // zero tail slots after compacted visible rows
            if (addon is not null && addon->AtkValues is not null)
                CrystallizeListAtk.WriteSlotsToAtkBuffer(workingAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, FilteredSlotCount, trailingClearThrough);
        }
        if (addon is not null && addon->AtkValues is not null)
            CrystallizeListAtk.WriteSlotsToAtkBuffer(workingAtk, addon->AtkValues, addon->AtkValuesCount, _bufferLayout, 0, FilteredSlotCount);
        Snapshot = CrystallizeListAtk.Clone(workingAtk); // keep Snapshot aligned with what tree now shows
        FinalizeTreeSlotCount(tree, FilteredSlotCount);
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

        var inferred = CrystallizeListAtk.InferItemCount(atkSnapshot, _bufferLayout);
        if (inferred <= 0)
            return;

        if (slotCount > inferred)
            slotCount = inferred;
        if (slotCount <= 0)
            return;

        fixed (AtkValue* snapshotPtr = atkSnapshot) {
            tree->LoadAtkValues(atkSnapshot.Length, snapshotPtr, _bufferLayout.UintValuesOffset, _bufferLayout.StringValuesOffset, _bufferLayout.UintValuesPerItem, _bufferLayout.StringValuesPerItem, slotCount, callback);
        }

        var itemCount = Math.Min(slotCount, tree->Items.Count);
        for (var slot = 0; slot < itemCount; slot++) {
            var item = tree->GetItem(slot);
            if (item is not null)
                CrystallizeListAtk.CopySlotToTreeItem(atkSnapshot, slot, item, _bufferLayout); // LoadAtkValues doesn't always fill renderer uint/string arrays
        }

        ShowItemRenderers(tree, itemCount);
        RefreshTreeListLayout(tree);
    }

    private static void ShowItemRenderers(AtkComponentTreeList* tree, int slotCount) {
        if (tree is null || slotCount <= 0)
            return;

        var list = (AtkComponentList*)tree;
        for (var slot = 0; slot < slotCount && slot < tree->Items.Count; slot++) {
            var renderer = list->GetItemRenderer(slot);
            if (renderer is null)
                continue;
            var owner = (AtkResNode*)renderer->OwnerNode;
            if (owner is not null)
                SetNodeVisible(owner, true);
        }
    }

    private static void HideAllItemRenderers(AtkComponentTreeList* tree) {
        if (tree is null)
            return;

        var list = (AtkComponentList*)tree;
        for (var slot = 0; slot < tree->Items.Count; slot++) {
            var renderer = list->GetItemRenderer(slot);
            if (renderer is null)
                continue;
            var owner = (AtkResNode*)renderer->OwnerNode;
            if (owner is not null)
                SetNodeVisible(owner, false);
        }
    }

    private static void FinalizeTreeSlotCount(AtkComponentTreeList* tree, int slotCount) {
        if (tree is null)
            return;

        var list = (AtkComponentList*)tree;
        var totalItems = tree->Items.Count;
        list->SetItemCount((short)Math.Max(0, slotCount));

        for (var slot = 0; slot < totalItems; slot++) {
            var item = tree->GetItem(slot);
            if (item is null)
                continue;

            var hidden = slot >= slotCount;
            item->IsHidden = hidden;
            if (hidden)
                CrystallizeListAtk.ClearTreeItemDisplay(item);
            else
                item->IsHidden = false;
        }
    }

    private static void ClearTreeDisplay(AtkComponentTreeList* tree) {
        if (tree is null)
            return;
        FinalizeTreeSlotCount(tree, 0);
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
