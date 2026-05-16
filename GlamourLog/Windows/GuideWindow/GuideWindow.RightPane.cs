using GlamourLog.Nodes.GuideWindow;
using KamiToolKit;

namespace GlamourLog.Windows.GuideWindow;

public unsafe partial class GuideWindow {
    private const float RightBlockSpacing = Constants.BlockSpacing;

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

        VerticalListEject.EjectAll(_rightScroll.VerticalListNode, _rightPaneBlocks);
        _pendingPaneDisposes.AddRange(_rightPaneBlocks);
        _rightPaneBlocks.Clear();

        var generation = _paneDisposeGeneration;
        Svc.Framework.RunOnTick(() => {
            if (generation != _paneDisposeGeneration || _isFinalizing || _isTearingDown)
                return;

            FlushPendingPaneDisposes();
        });
    }

    private void AppendRightPaneBlock(ContentBlock block) {
        if (_rightScroll is null)
            return;

        var node = block switch {
            GuideTextBlock text => (NodeBase)new ParagraphNode(_rightTextWidth, text.Text, text.TextLeftInset, text.TextBoxHeight),
            GuideHeadingBlock heading => new SectionTitleNode(_rightTextWidth, heading.Title),
            IconExampleBlock icon => new IconSampleRowNode(_rightTextWidth, icon.Kind, icon.Description, icon.TextBoxHeight),
            CheckboxSettingBlock setting => new ConfigCheckboxRowNode(_rightTextWidth, setting),
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
                case ParagraphNode text:
                    text.Relayout(layoutWidth);
                    break;
                case IconSampleRowNode icon:
                    icon.Relayout(layoutWidth);
                    break;
                case ConfigCheckboxRowNode setting:
                    setting.Relayout(layoutWidth);
                    break;
            }
        }
    }

    private void RebuildRightPane(Page page) {
        EjectRightPaneBlocks();

        foreach (var block in page.EnumerateBlocks())
            AppendRightPaneBlock(block);

        RelayoutRightPaneBlocks();
    }
}
