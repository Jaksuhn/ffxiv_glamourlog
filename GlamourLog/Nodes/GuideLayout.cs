namespace GlamourLog.Nodes;

internal static class GuideLayout {
    /// <summary>Axis body copy (line spacing matches the native glamour log help / Axis 14 rhythm).</summary>
    internal const uint GuideBodyFontSize = 14;
    internal const uint GuideBodyLineSpacing = 24;
    internal const float IconColumnWidth = 29f;
    internal const float IconTextGap = 10f;
    internal const float IconTextLeft = IconColumnWidth + IconTextGap;

    internal const float RowPadTop = 4f;
    internal const float RowPadBottom = 4f;
    internal const float BlockSpacing = 4f;

    /// <summary>Pull Axis 14 copy up so the first line lines up with icon tops.</summary>
    internal const float TextTopInset = -2f;

    /// <summary><see cref="FramedItemIconNode"/> frame draws above its layout origin by this much.</summary>
    internal const float FramedItemFrameBleed = 4f;

    /// <summary>
    /// Fixed <see cref="KamiToolKit.Nodes.TextNode"/> height for body copy. Text wraps inside; list row height does not follow line count.
    /// Override per page via <see cref="GuidePage.BodyTextBoxHeight"/> or <see cref="GuideTextBlock.TextBoxHeight"/>.
    /// </summary>
    internal const float DefaultGuideBodyTextBoxHeight = 200f;

    /// <summary>Fixed text height beside icon samples (Icons guide).</summary>
    internal const float DefaultGuideIconExampleTextBoxHeight = 50f;
}
