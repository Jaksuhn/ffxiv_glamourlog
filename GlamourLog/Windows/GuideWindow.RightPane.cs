using GlamourLog.Nodes;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace GlamourLog;

public unsafe partial class GuideWindow {
    private const float RightBlockSpacing = GuideLayout.BlockSpacing;

    private readonly List<NodeBase> _rightPaneBlocks = [];
    private readonly List<NodeBase> _pendingPaneDisposes = [];
    private int _paneDisposeGeneration;

    private void CancelDeferredPaneDisposes() => _paneDisposeGeneration++;

    private void FlushPendingPaneDisposes() {
        if (_pendingPaneDisposes.Count == 0)
            return;

        foreach (var node in _pendingPaneDisposes.ToList())
            node.Dispose();

        _pendingPaneDisposes.Clear();
    }

    private void EjectRightPaneBlocks() {
        if (_isFinalizing || _isTearingDown || _rightScroll is null || _rightPaneBlocks.Count == 0)
            return;

        GuideListNodeHelper.EjectAll(_rightScroll.VerticalListNode, _rightPaneBlocks);
        _pendingPaneDisposes.AddRange(_rightPaneBlocks);
        _rightPaneBlocks.Clear();

        var generation = _paneDisposeGeneration;
        Svc.Framework.RunOnTick(() => {
            if (generation != _paneDisposeGeneration || _isFinalizing || _isTearingDown)
                return;

            FlushPendingPaneDisposes();
        });
    }

    private void AppendRightPaneBlock(GuideContentBlock block) {
        if (_rightScroll is null)
            return;

        var node = block switch {
            GuideTextBlock text => (NodeBase)new GuideTextBlockNode(_rightTextWidth, text.Text, text.TextLeftInset, text.TextBoxHeight),
            GuideHeadingBlock heading => new GuideHeadingBlockNode(_rightTextWidth, heading.Title),
            GuideIconExampleBlock icon => new GuideIconExampleNode(_rightTextWidth, icon.Kind, icon.Description, icon.TextBoxHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(block)),
        };

        _rightScroll.AddNode(node);
        _rightPaneBlocks.Add(node);
    }

    private void RelayoutRightPaneBlocks() {
        if (_isFinalizing || _isTearingDown || _rightScroll is null)
            return;

        var width = _rightScroll.ContentWidth;
        var layoutWidth = Math.Min(_rightTextWidth, width);

        foreach (var node in _rightPaneBlocks) {
            switch (node) {
                case GuideTextBlockNode text:
                    text.Relayout(layoutWidth);
                    break;
                case GuideIconExampleNode icon:
                    icon.Relayout(layoutWidth);
                    break;
            }
        }
    }

    private void RebuildRightPane(GuidePage page) {
        EjectRightPaneBlocks();

        foreach (var block in page.EnumerateBlocks())
            AppendRightPaneBlock(block);

        RelayoutRightPaneBlocks();
    }
}
