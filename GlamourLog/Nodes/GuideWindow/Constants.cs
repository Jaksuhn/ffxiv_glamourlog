namespace GlamourLog.Nodes.GuideWindow;

internal static class Constants {
    internal const uint GuideBodyFontSize = 14;
    internal const uint GuideBodyLineSpacing = 24;
    internal const float IconColumnWidth = 29f;
    internal const float IconTextGap = 10f;
    internal const float IconTextLeft = IconColumnWidth + IconTextGap;

    internal const float RowPadTop = 4f;
    internal const float RowPadBottom = 4f;
    internal const float BlockSpacing = 4f;

    internal const float TextTopInset = -2f; // needed so text is flush with top of icons
    internal const float FramedItemFrameBleed = 4f; // FramedItemIconNode's frame draws above the item's origin by this much. Used to add to the top row padding so things stay aligned

    internal const float DefaultGuideBodyTextBoxHeight = 200f; // fixed height textbox size because I couldn't get word wrapping to work normally. Or at all tbh. Can be override in GuidePage/GuideTextBlock
    internal const float DefaultGuideIconExampleTextBoxHeight = 50f; // same as above but for the icon descriptions
}
