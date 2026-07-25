using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes.GuideWindow;

internal static class GuideTextLayout {
    internal static float MeasureWrappedHeight(TextNode text, ReadOnlySeString content, float textWidth) {
        var width = Math.Max(40f, textWidth);
        text.Size = new Vector2(width, 2000f); // arbitrary large height to avoid being height-clipped
        text.String = content;
        return Math.Max(Constants.GuideBodyFontSize, text.GetTextDrawSize().Y);
    }
}
