using Lumina.Text.ReadOnly;

namespace GlamourLog.Windows.GuideWindow;

/// <summary>One right-pane topic; add a matching GuideWindow.*.cs partial per page.</summary>
internal sealed class Page {
    public required string CategoryTitle { get; init; }
    public required string SubCategoryTitle { get; init; }
    public ReadOnlySeString Body { get; init; } = string.Empty;

    /// <summary>When empty, <see cref="Body"/> is shown as a single text block.</summary>
    public IReadOnlyList<ContentBlock> Blocks { get; init; } = [];

    /// <summary>Fixed text box height for the implicit <see cref="GuideTextBlock"/> when <see cref="Blocks"/> is empty.</summary>
    public float? BodyTextBoxHeight { get; init; }

    internal IEnumerable<ContentBlock> EnumerateBlocks()
        => Blocks.Count > 0
            ? Blocks
            : [new GuideTextBlock(Body, TextLeftInset: 0f, TextBoxHeight: BodyTextBoxHeight)];
}
