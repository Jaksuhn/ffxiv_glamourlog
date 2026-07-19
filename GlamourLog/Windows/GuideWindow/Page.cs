using Lumina.Text.ReadOnly;

namespace GlamourLog.Windows.GuideWindow;

// one topic in the guide's right pane (each GuideWindow.*.cs partial builds one of these)
internal sealed class Page {
    public required string CategoryTitle { get; init; }
    public required string SubCategoryTitle { get; init; }
    public ReadOnlySeString Body { get; init; } = string.Empty;
    public IReadOnlyList<ContentBlock> Blocks { get; init; } = []; // when empty, body will be a single text block
    public float? BodyTextBoxHeight { get; init; } // fixed height for the implicit TextBlock when Blocks is empty

    internal IEnumerable<ContentBlock> EnumerateBlocks()
        => Blocks.Count > 0 ? Blocks : [new GuideTextBlock(Body, TextLeftInset: 0f, TextBoxHeight: BodyTextBoxHeight)];
}
