#if DEBUG
using GlamourLog.Nodes.GuideWindow;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class CircleButtonsDebugGuidePage : IGuidePage {
    public string Id => "debug.circle-buttons";
    public GuideCategory Category => GuideCategory.Debug;
    public int Order => 0;
    public string Title => "Circle Buttons";

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [new CircleButtonGalleryBlock()];

    private sealed record CircleButtonGalleryBlock : GuideBlock<CircleButtonGalleryNode> {
        protected override CircleButtonGalleryNode Create(float width) => new(width);
        protected override void Relayout(CircleButtonGalleryNode node, float width) => node.Relayout(width);
    }
}
#endif
