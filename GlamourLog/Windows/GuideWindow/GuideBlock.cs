using GlamourLog.Nodes.GuideWindow;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Windows.GuideWindow;

internal interface IGuideBlock {
    NodeBase CreateNode(float width);
    void Relayout(NodeBase node, float width);
}

internal abstract record GuideBlock<TNode> : IGuideBlock where TNode : NodeBase {
    protected abstract TNode Create(float width);
    protected virtual void Relayout(TNode node, float width) { }

    NodeBase IGuideBlock.CreateNode(float width) => Create(width);
    void IGuideBlock.Relayout(NodeBase node, float width) => Relayout((TNode)node, width);
}

internal sealed record GuideTextBlock(ReadOnlySeString Text, float TextLeftInset = 0f) : GuideBlock<ParagraphNode> {
    protected override ParagraphNode Create(float width) => new(width, Text, TextLeftInset);
    protected override void Relayout(ParagraphNode node, float width) => node.Relayout(width);
}

internal sealed record GuideHeadingBlock(string Title) : GuideBlock<SectionTitleNode> {
    protected override SectionTitleNode Create(float width) => new(width, Title);
}

internal sealed record IconExampleBlock(IconExampleKind Kind, ReadOnlySeString Description) : GuideBlock<IconSampleRowNode> {
    protected override IconSampleRowNode Create(float width) => new(width, Kind, Description);
    protected override void Relayout(IconSampleRowNode node, float width) => node.Relayout(width);
}

internal sealed record CircleButtonExampleBlock(CircleButtonIcon Icon, ReadOnlySeString Description) : GuideBlock<CircleButtonSampleRowNode> {
    protected override CircleButtonSampleRowNode Create(float width) => new(width, Icon, Description);
    protected override void Relayout(CircleButtonSampleRowNode node, float width) => node.Relayout(width);
}

internal enum IconExampleKind {
    Checkmark,
    FadedDresser,
    Dresser,
    Armoire,
    WarningDresser,
    Unobtainable,
}
