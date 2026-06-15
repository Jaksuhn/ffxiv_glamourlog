using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes;

// virtualized detail column (ala ListNode).
// not ListNode<>: assignments use ListItemNode.ItemData, so a derived setter never runs; same-ref skips leave pooled text stale (PopulateNodes).
internal sealed unsafe class DetailRowsListNode : SimpleComponentNode {
    public readonly ScrollBarNode ScrollBarNode;
    internal TextNode DutyChestMeasureNode => _dutyChestMeasureNode;

    private const float ScrollBarWidth = 8f;
    private const float RowWidthInset = 16f;

    public IReadOnlyList<DetailListItemNode> OptionNodes => nodeList;

    public void SyncRowWidths() {
        var rowWidth = Math.Max(0f, Width - RowWidthInset);
        foreach (var node in nodeList) {
            if (Math.Abs(node.Width - rowWidth) > 0.5f)
                node.Width = rowWidth;
        }
    }

    private readonly List<DetailListItemNode> nodeList = [];
    private readonly TextNode _dutyChestMeasureNode;
    private readonly float itemHeight = DetailListItemNode.ItemHeight;
    private DetailListRowData? selectedItem;
    private int scrollPosition;
    private int nodeCount;
    private int[] lastDataIndexBySlot = [];
    private bool needsPostNativeRebind;

    public DetailRowsListNode() {
        _dutyChestMeasureNode = new TextNode {
            FontSize = 12,
            LineSpacing = 12,
            FontType = FontType.Axis,
            IsVisible = false,
        };
        _dutyChestMeasureNode.AttachNode(this);

        ScrollBarNode = new ScrollBarNode {
            OnValueChanged = OnScrollUpdate,
            ScrollSpeed = (int)itemHeight,
            HideWhenDisabled = true,
        };
        ScrollBarNode.AttachNode(this);

        // ktk ListNode: wheel on list body doesn't scroll unless it's handled here
        AddEvent(AtkEventType.MouseWheel, OnMouseWheel);
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();

        ScrollBarNode.Size = new Vector2(ScrollBarWidth, Height);
        ScrollBarNode.Position = new Vector2(Width - ScrollBarWidth, 0.0f);

        var newNodeCount = (int)(Height / (itemHeight + ItemSpacing));
        if (newNodeCount != nodeCount)
            FullRebuild();

        SyncRowWidths();

        RecalculateScroll();
    }

    public Action<DetailListRowData?>? OnItemSelected { get; set; }

    public Action<uint>? OnPieceLeftClick { get; set; }
    public Action<uint>? OnItemRightClick { get; set; }
    public Action<uint, SourceNavigateTarget?>? OnSourceHeaderRightClick { get; set; }
    public Action<SourceNavigateTarget, string>? OnSourceMapFlagLeftClick { get; set; }
    public Action<uint>? OnCraftRecipeJournalLeftClick { get; set; }
    public Action<string, bool>? OnDetailSectionToggle { get; set; }
    public Func<string, bool>? IsDetailSectionCollapsed { get; set; }
    public Action<GlamourSet>? OnSharedModelSetLeftClick { get; set; }
    public Action<uint, GlamourSet>? OnSharedModelItemLeftClick { get; set; }

    public float ItemSpacing {
        get;
        set {
            field = value;
            FullRebuild();
        }
    }

    public required List<DetailListRowData> OptionsList {
        get;
        set {
            field = value;
            needsPostNativeRebind = true;

            var newNodeCount = (int)(Height / (itemHeight + ItemSpacing));
            if (newNodeCount != nodeCount)
                FullRebuild();
            else {
                PopulateNodes();
                RecalculateScroll();
            }
        }
    } = [];

    public void ResetScrollToTop() {
        scrollPosition = 0;
        ScrollBarNode.ScrollPosition = 0;
        ResetSlotIndexMemory();
        RecalculateScroll();
        // don't PopulateNodes here: caller replaces OptionsList next frame; repainting the old
        // list re-applies SourceChest narrow text widths that atk keeps until scroll/rebind
    }

    public void AttachInteractivity() {
        ScrollBarNode.OnValueChanged = OnScrollUpdate;
        SyncNodeCallbacks();
    }

    public void DetachInteractivity() {
        ScrollBarNode.OnValueChanged = null;
        OnItemSelected = null;
        OnPieceLeftClick = null;
        OnItemRightClick = null;
        OnSourceHeaderRightClick = null;
        OnSourceMapFlagLeftClick = null;
        OnCraftRecipeJournalLeftClick = null;
        OnDetailSectionToggle = null;
        IsDetailSectionCollapsed = null;
        OnSharedModelSetLeftClick = null;
        OnSharedModelItemLeftClick = null;
        SyncNodeCallbacks();
    }

    public void PrepareForClose() {
        DetachInteractivity();
        ResetNativeScrollPosition();
    }

    private void ResetNativeScrollPosition() {
        var bar = (AtkComponentScrollBar*)ScrollBarNode;
        bar->IsBeingDragged = false;
        bar->SetContentNode(null, null);
        bar->SetScrollPosition(0);
        scrollPosition = 0;
    }

    public void FullRebuild() {
        foreach (var node in nodeList)
            node.Dispose();
        nodeList.Clear();

        scrollPosition = Math.Clamp(scrollPosition, 0, Math.Max(OptionsList.Count - nodeCount, 0));
        selectedItem = null;

        RebuildNodeList();
        ResetSlotIndexMemory();
        PopulateNodes();
        RecalculateScroll();
    }

    public void Update() {
        if (OnItemSelected is null)
            return;

        // post-native pass: pre-native populate + atk draw can leave SourceChest narrow text metrics on Cost rows
        if (needsPostNativeRebind) {
            PopulateNodes(forceRebind: true);
            needsPostNativeRebind = false;
        }
        else
            PopulateNodes();

        foreach (var node in nodeList) {
            if (node.IsVisible)
                node.Update();
        }
    }

    private void RebuildNodeList() {
        nodeCount = (int)(Height / (itemHeight + ItemSpacing));
        if (nodeCount < 1)
            return;

        foreach (var index in Enumerable.Range(0, nodeCount)) {
            var node = new DetailListItemNode {
                Size = new Vector2(Math.Max(0f, Width - RowWidthInset), itemHeight),
                Position = new Vector2(0.0f, index * (itemHeight + ItemSpacing)),
                // ktk pool convention: stable ids for addon node table (matches ListNode)
                NodeId = (uint)index + 2,
                OnClick = clickedNode => {
                    SelectItem(((DetailListItemNode)clickedNode).ItemData);
                    OnItemSelected?.Invoke(selectedItem);
                },
                IsVisible = false,
            };
            node.AttachNode(this);
            WireNode(node);
            nodeList.Add(node);
        }
    }

    private void WireNode(DetailListItemNode node) {
        node.OnPieceLeftClick = OnPieceLeftClick;
        node.OnItemRightClick = OnItemRightClick;
        node.OnSourceHeaderRightClick = OnSourceHeaderRightClick;
        node.OnSourceMapFlagLeftClick = OnSourceMapFlagLeftClick;
        node.OnCraftRecipeJournalLeftClick = OnCraftRecipeJournalLeftClick;
        node.OnDetailSectionToggle = OnDetailSectionToggle;
        node.IsDetailSectionCollapsed = IsDetailSectionCollapsed;
        node.OnSharedModelSetLeftClick = OnSharedModelSetLeftClick;
        node.OnSharedModelItemLeftClick = OnSharedModelItemLeftClick;
    }

    private void SyncNodeCallbacks() {
        foreach (var node in nodeList)
            WireNode(node);
    }

    private void ResetSlotIndexMemory() {
        lastDataIndexBySlot = new int[nodeList.Count];
        for (var i = 0; i < lastDataIndexBySlot.Length; i++)
            lastDataIndexBySlot[i] = -1;
    }

    private void PopulateNodes(bool forceRebind = false) {
        if (lastDataIndexBySlot.Length != nodeList.Count)
            ResetSlotIndexMemory();

        for (var nodeIndex = 0; nodeIndex < nodeList.Count; nodeIndex++) {
            var node = nodeList[nodeIndex];
            var dataIndex = scrollPosition + nodeIndex;

            if (dataIndex < OptionsList.Count) {
                var item = OptionsList[dataIndex];
                var prevDataIndex = lastDataIndexBySlot[nodeIndex];
                lastDataIndexBySlot[nodeIndex] = dataIndex;

                // null first forces SetNodeData: same ref + same slot index skips in ListItemNode; piece/shared-model rows need every-frame refresh too
                var prevKind = node.ItemData?.Kind;
                if (forceRebind
                    || !ReferenceEquals(node.ItemData, item)
                    || prevDataIndex != dataIndex
                    || prevKind != item.Kind
                    || item.Kind is DetailRowKind.Piece or DetailRowKind.SharedModelSet)
                    node.ItemData = null;

                node.ItemData = item;
                node.IsVisible = true;
                node.IsSelected = ReferenceEquals(item, selectedItem);
            }
            else {
                node.IsVisible = false;
            }
        }
    }

    private void SelectItem(DetailListRowData? item) {
        if (item is null)
            return;

        selectedItem = item;

        foreach (var node in nodeList) {
            if (node.ItemData is null)
                node.IsSelected = false;
            else
                node.IsSelected = ReferenceEquals(node.ItemData, selectedItem);
        }
    }

    private void RecalculateScroll() {
        scrollPosition = Math.Clamp(scrollPosition, 0, Math.Max(0, OptionsList.Count - nodeCount));

        if (OptionsList.Count < nodeCount) {
            ScrollBarNode.ScrollPosition = 0;
            ScrollBarNode.IsEnabled = false;
        }

        var totalHeight = (int)(OptionsList.Count * (itemHeight + ItemSpacing) + ItemSpacing);
        ScrollBarNode.UpdateScrollParams((int)(nodeList.Count * (itemHeight + ItemSpacing)), totalHeight);
        ScrollBarNode.ScrollPosition = (int)(scrollPosition * (itemHeight + ItemSpacing));
    }

    private void OnScrollUpdate(int newPosition) {
        scrollPosition = (int)(newPosition / (itemHeight + ItemSpacing));
        PopulateNodes();
    }

    private void OnMouseWheel(AtkEventListener* thisPtr, AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData) {
        scrollPosition += atkEventData->IsScrollUp ? -1 : 1;
        scrollPosition = Math.Clamp(scrollPosition, 0, Math.Max(0, OptionsList.Count - nodeCount));

        ScrollBarNode.ScrollPosition = (int)(scrollPosition * (itemHeight + ItemSpacing));
        PopulateNodes();

        atkEvent->SetEventIsHandled();
    }
}
