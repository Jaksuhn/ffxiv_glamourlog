using GlamourLog.Nodes.GuideWindow;
using KamiToolKit;

namespace GlamourLog.Windows.GuideWindow;

public unsafe partial class GuideWindow {
    private const float RightBlockSpacing = Constants.BlockSpacing;

    private readonly Dictionary<Page, List<NodeBase>> _pageBlocks = [];

    private void BuildAllRightPanePages() {
        if (_rightScroll is null)
            return;

        _pageBlocks.Clear();
        foreach (var category in NavCategories) {
            foreach (var page in category.Pages)
                _pageBlocks[page] = AddPageBlocks(page);
        }
    }

    private List<NodeBase> AddPageBlocks(Page page) {
        var scroll = _rightScroll!;
        var blocks = new List<NodeBase>();
        foreach (var block in page.EnumerateBlocks()) {
            var node = CreateRightPaneBlock(block);
            node.IsVisible = false;
            scroll.AddNode(node);
            blocks.Add(node);
        }

        return blocks;
    }

    private NodeBase CreateRightPaneBlock(ContentBlock block) => block switch {
        GuideTextBlock text => new ParagraphNode(_rightTextWidth, text.Text, text.TextLeftInset, text.TextBoxHeight),
        GuideHeadingBlock heading => new SectionTitleNode(_rightTextWidth, heading.Title),
        IconExampleBlock icon => new IconSampleRowNode(_rightTextWidth, icon.Kind, icon.Description, icon.TextBoxHeight),
        CheckboxSettingBlock setting => new ConfigCheckboxRowNode(_rightTextWidth, setting),
        _ => throw new ArgumentOutOfRangeException(nameof(block)),
    };

    private void ShowRightPanePage(Page page) {
        foreach (var (knownPage, nodes) in _pageBlocks) {
            var visible = ReferenceEquals(knownPage, page);
            foreach (var node in nodes)
                node.IsVisible = visible;
        }
    }

    private void RelayoutVisibleRightPaneBlocks() {
        if (_isFinalizing || _rightScroll is null || !_pageBlocks.TryGetValue(_selectedPage, out var nodes))
            return;

        var layoutWidth = Math.Min(_rightTextWidth, _rightScroll.ContentWidth);
        foreach (var node in nodes) {
            if (!node.IsVisible)
                continue;

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
}
