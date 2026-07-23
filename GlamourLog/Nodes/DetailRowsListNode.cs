using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed unsafe class DetailRowsListNode : ListNode<DetailListRowData, DetailListItemNode> {
    private const float RowWidthInset = 16f;

    internal TextNode DutyChestMeasureNode { get; }

    private bool needsPostNativeRebind; // native list recycling can wipe callbacks after OptionsList changes

    public Action<uint>? OnPieceLeftClick { get; set; }
    public Action<uint>? OnItemRightClick { get; set; }
    public Action<uint, SourceNavigateTarget?>? OnSourceHeaderRightClick { get; set; }
    public Action<SourceNavigateTarget, string>? OnSourceMapFlagLeftClick { get; set; }
    public Action<uint, string>? OnSourceChestMapLeftClick { get; set; }
    public Action<uint>? OnCraftRecipeJournalLeftClick { get; set; }
    public Action<string, bool>? OnDetailSectionToggle { get; set; }
    public Func<string, bool>? IsDetailSectionCollapsed { get; set; }
    public Action<GlamourSet>? OnSharedModelSetLeftClick { get; set; }
    public Action<uint, GlamourSet>? OnSharedModelItemLeftClick { get; set; }

    public DetailRowsListNode() {
        AutoResetScroll = false;

        DutyChestMeasureNode = new TextNode {
            FontSize = 12,
            LineSpacing = 12,
            FontType = FontType.Axis,
            IsVisible = false,
        };
        DutyChestMeasureNode.AttachNode(this);
    }

    public void AssignOptionsList(List<DetailListRowData> options) {
        needsPostNativeRebind = true;
        OptionsList = options;
        SyncNodeCallbacks();
    }

    public void SyncRowWidths() {
        var rowWidth = Math.Max(0f, Width - RowWidthInset);
        foreach (var node in OptionNodes) {
            if (Math.Abs(node.Width - rowWidth) > 0.5f)
                node.Width = rowWidth;
        }
    }

    public void ResetScrollToTop() => ResetScroll();

    public void AttachInteractivity() => SyncNodeCallbacks();

    public void DetachInteractivity() {
        OnItemSelected = null;
        OnPieceLeftClick = null;
        OnItemRightClick = null;
        OnSourceHeaderRightClick = null;
        OnSourceMapFlagLeftClick = null;
        OnSourceChestMapLeftClick = null;
        OnCraftRecipeJournalLeftClick = null;
        OnDetailSectionToggle = null;
        IsDetailSectionCollapsed = null;
        OnSharedModelSetLeftClick = null;
        OnSharedModelItemLeftClick = null;
        SyncNodeCallbacks();
    }

    public void PrepareForClose() {
        DetachInteractivity();
        var bar = (AtkComponentScrollBar*)ScrollBarNode;
        bar->IsBeingDragged = false;
        bar->SetContentNode(null, null);
        bar->SetScrollPosition(0);
    }

    public new void FullRebuild() {
        base.FullRebuild();
        SyncNodeCallbacks();
    }

    public new void Update() {
        RefreshDynamicRows();

        if (needsPostNativeRebind) {
            ForceRebindVisibleNodes();
            needsPostNativeRebind = false;
        }

        base.Update();
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        SyncRowWidths();
        SyncNodeCallbacks();
    }

    private void RefreshDynamicRows() {
        foreach (var node in OptionNodes) {
            if (!node.IsVisible || node.ItemData is not { } item)
                continue;
            if (item.Kind is not (DetailRowKind.Piece or DetailRowKind.SharedModelSet))
                continue;

            node.ItemData = null;
            node.ItemData = item;
        }
    }

    private void ForceRebindVisibleNodes() {
        foreach (var node in OptionNodes) {
            if (!node.IsVisible)
                continue;

            var item = node.ItemData;
            node.ItemData = null;
            if (item is not null)
                node.ItemData = item;
        }
    }

    private void SyncNodeCallbacks() {
        foreach (var node in OptionNodes)
            WireNode(node);
    }

    private void WireNode(DetailListItemNode node) {
        node.OnPieceLeftClick = OnPieceLeftClick;
        node.OnItemRightClick = OnItemRightClick;
        node.OnSourceHeaderRightClick = OnSourceHeaderRightClick;
        node.OnSourceMapFlagLeftClick = OnSourceMapFlagLeftClick;
        node.OnSourceChestMapLeftClick = OnSourceChestMapLeftClick;
        node.OnCraftRecipeJournalLeftClick = OnCraftRecipeJournalLeftClick;
        node.OnDetailSectionToggle = OnDetailSectionToggle;
        node.IsDetailSectionCollapsed = IsDetailSectionCollapsed;
        node.OnSharedModelSetLeftClick = OnSharedModelSetLeftClick;
        node.OnSharedModelItemLeftClick = OnSharedModelItemLeftClick;
    }
}
