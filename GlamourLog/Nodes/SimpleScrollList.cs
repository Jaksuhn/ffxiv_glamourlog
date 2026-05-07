using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

public static class SimpleScrollList {
    public static ScrollingListNode Create(Vector2 position, Vector2 size, bool autoHideScrollBar) => new() {
        Position = position,
        Size = size,
        ItemSpacing = 0f,
        FitWidth = true,
        ClipListContents = true,
        AutoHideScrollBar = autoHideScrollBar,
    };
}
