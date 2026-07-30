using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed unsafe class DetailRowsListNode : NestableTreeListNode<DetailListRowData, DetailListItemNode> {
    private const float RowWidthInset = 16f;

    internal TextNode DutyChestMeasureNode { get; }

    public Action<uint>? OnPieceLeftClick { get; set; }
    public Action<uint>? OnItemRightClick { get; set; }
    public Action<uint, SourceNavigateTarget?>? OnSourceHeaderRightClick { get; set; }
    public Action<SourceNavigateTarget, string>? OnSourceMapFlagLeftClick { get; set; }
    public Action<uint, string>? OnSourceChestMapLeftClick { get; set; }
    public Action<uint>? OnCraftRecipeJournalLeftClick { get; set; }
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

    public void AssignSections(List<TreeListSection<DetailListRowData>> sections) {
        Sections = sections;
        SyncNodeCallbacks();
    }

    public void SyncRowWidths() {
        var rowWidth = Math.Max(0f, Width - RowWidthInset);
        foreach (var node in EntryNodes) {
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

    public new void Update() {
        foreach (var node in EntryNodes) {
            if (!node.IsVisible || node.ItemData is not { } item)
                continue;
            if (item.Kind is not (DetailRowKind.Piece or DetailRowKind.SharedModelSet))
                continue;

            node.ItemData = null;
            node.ItemData = item;
        }

        SyncNodeCallbacks();
        base.Update();
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        SyncRowWidths();
        SyncNodeCallbacks();
    }

    private void SyncNodeCallbacks() {
        foreach (var node in EntryNodes)
            WireNode(node);
    }

    private void WireNode(DetailListItemNode node) {
        node.OnPieceLeftClick = OnPieceLeftClick;
        node.OnItemRightClick = OnItemRightClick;
        node.OnSourceHeaderRightClick = OnSourceHeaderRightClick;
        node.OnSourceMapFlagLeftClick = OnSourceMapFlagLeftClick;
        node.OnSourceChestMapLeftClick = OnSourceChestMapLeftClick;
        node.OnCraftRecipeJournalLeftClick = OnCraftRecipeJournalLeftClick;
        node.OnSharedModelSetLeftClick = OnSharedModelSetLeftClick;
        node.OnSharedModelItemLeftClick = OnSharedModelItemLeftClick;
    }
}
