namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page DebugCircleButtons = new() {
        CategoryTitle = "Debug",
        SubCategoryTitle = "Circle Buttons",
        Blocks = [
            new CircleButtonGalleryBlock(),
        ],
    };
}
