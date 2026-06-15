using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public static class SimpleScrollList {
    // ScrollingAreaNode (KTK 1.x) reserved 16px for the scrollbar track + gap.
    public const float ScrollbarContentInset = 16f;

    public static ScrollingNode<VerticalListNode> Create(Vector2 position, Vector2 size, bool autoHideScrollBar) {
        var scroll = new MarginScrollingNode {
            Position = position,
            AutoHideScrollBar = autoHideScrollBar,
        };
        scroll.ContentNode.ItemSpacing = 0f;
        scroll.ContentNode.FitWidth = true;
        scroll.ContentNode.FitContents = true;
        scroll.Size = size;
        return scroll;
    }

    private sealed class MarginScrollingNode : ScrollingNode<VerticalListNode> {
        protected override void OnSizeChanged() {
            base.OnSizeChanged();

            var contentWidth = Math.Max(0f, Width - ScrollbarContentInset);
            ClippingContentNode.Size = new Vector2(contentWidth, Height);
            ScrollingCollisionNode.Size = new Vector2(contentWidth, Height);
            ContentNode.Width = contentWidth;

            if (ContentNode is LayoutListNode layout)
                layout.RecalculateLayout();
        }
    }
}
