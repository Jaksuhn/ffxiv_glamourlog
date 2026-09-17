using GlamourLog.Nodes.GuideWindow;
using KamiToolKit.BaseTypes;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private const float RightBlockSpacing = Constants.BlockSpacing;

    // only the active page's nodes live in the scroll list — hiding siblings leaves the child component collisions steals clicks
    private readonly List<(NodeBase Node, IGuideBlock Block)> _rightPaneBlocks = [];

    private void RebuildRightPanePage(IGuidePage page) {
        if (_rightScroll is null)
            return;

        _rightScroll.ContentNode.Clear();
        _rightPaneBlocks.Clear();

        foreach (var block in page.BuildBlocks(_pageContext)) {
            var node = block.CreateNode(_rightTextWidth);
            _rightScroll.ContentNode.AddNode(node);
            _rightPaneBlocks.Add((node, block));
        }
    }

    private void RelayoutRightPaneBlocks() {
        if (_rightScroll is null)
            return;

        var layoutWidth = Math.Min(_rightTextWidth, _rightScroll.ContentNode.Width);
        foreach (var (node, block) in _rightPaneBlocks)
            block.Relayout(node, layoutWidth);
    }
}
