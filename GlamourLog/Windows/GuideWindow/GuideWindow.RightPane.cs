using GlamourLog.Nodes.GuideWindow;
using KamiToolKit.BaseTypes;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private const float RightBlockSpacing = Constants.BlockSpacing;

    // only the active page's nodes live in the scroll list — hiding siblings leaves the child component collisions steals clicks
    private readonly List<NodeBase> _rightPaneBlocks = [];

    private void RebuildRightPanePage(Page page) {
        if (_rightScroll is null)
            return;

        _rightScroll.ContentNode.Clear();
        _rightPaneBlocks.Clear();

        foreach (var block in page.EnumerateBlocks()) {
            var node = CreateRightPaneBlock(block);
            _rightScroll.ContentNode.AddNode(node);
            _rightPaneBlocks.Add(node);
        }
    }

    private NodeBase CreateRightPaneBlock(ContentBlock block) => block switch {
        GuideTextBlock text => new ParagraphNode(_rightTextWidth, text.Text, text.TextLeftInset),
        GuideHeadingBlock heading => new SectionTitleNode(_rightTextWidth, heading.Title),
        IconExampleBlock icon => new IconSampleRowNode(_rightTextWidth, icon.Kind, icon.Description),
        CircleButtonExampleBlock circle => new CircleButtonSampleRowNode(_rightTextWidth, circle.Icon, circle.Description),
        CheckboxSettingBlock setting => new ConfigCheckboxRowNode(_rightTextWidth, setting),
        CircleButtonGalleryBlock => new CircleButtonGalleryNode(_rightTextWidth),
        DataExportActionBlock export => new DataExportRowNode(_rightTextWidth, export.Format, () => CopyDataExportToClipboard(export.Format)),
        _ => throw new ArgumentOutOfRangeException(nameof(block)),
    };

    private void RelayoutRightPaneBlocks() {
        if (_rightScroll is null)
            return;

        var layoutWidth = Math.Min(_rightTextWidth, _rightScroll.ContentNode.Width);
        foreach (var node in _rightPaneBlocks) {
            switch (node) {
                case ParagraphNode text:
                    text.Relayout(layoutWidth);
                    break;
                case IconSampleRowNode icon:
                    icon.Relayout(layoutWidth);
                    break;
                case CircleButtonSampleRowNode circle:
                    circle.Relayout(layoutWidth);
                    break;
                case ConfigCheckboxRowNode setting:
                    setting.Relayout(layoutWidth);
                    break;
                case CircleButtonGalleryNode gallery:
                    gallery.Relayout(layoutWidth);
                    break;
                case DataExportRowNode export:
                    export.Relayout(layoutWidth);
                    break;
            }
        }
    }
}
