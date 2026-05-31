using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;

namespace GlamourLog.Nodes;

// virtualized detail column (ala ListNode).
// not ListNode<>: assignments use ListItemNode.ItemData, so a derived setter never runs; same-ref skips leave pooled text stale (PopulateNodes).
internal sealed unsafe class DetailRowsListNode : SimpleComponentNode {
    public readonly ScrollBarNode ScrollBarNode;

    private readonly List<DetailListItemNode> nodeList = [];
    private readonly float itemHeight = DetailListItemNode.ItemHeight;
    private DetailListRowData? selectedItem;
    private int scrollPosition;
    private int nodeCount;
    private int[] lastDataIndexBySlot = [];
    private bool needsPostNativeRebind;

    public DetailRowsListNode() {
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

        ScrollBarNode.Size = new Vector2(8.0f, Height);
        ScrollBarNode.Position = new Vector2(Width - 8.0f, 0.0f);

        var newNodeCount = (int)(Height / (itemHeight + ItemSpacing));
        if (newNodeCount != nodeCount)
            FullRebuild();

        // kami ListNode: row width = space left of scrollbar track (scrollbar still reserves 8px at right)
        foreach (var node in nodeList)
            node.Width = ScrollBarNode.Bounds.Left - 8.0f;

        RecalculateScroll();
    }

    public Action<DetailListRowData?>? OnItemSelected { get; set; }

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
                Size = new Vector2(ScrollBarNode.Bounds.Left - 8.0f, itemHeight),
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
            nodeList.Add(node);
        }
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
        ScrollBarNode.IsEnabled = OptionsList.Count > nodeCount;

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