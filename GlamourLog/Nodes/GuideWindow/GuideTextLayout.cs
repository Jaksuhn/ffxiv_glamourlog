using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes.GuideWindow;

internal static class GuideTextLayout {
    // TextNode.Size triggers UpdateText which triggers SetText(GetText()). GetText() is already word-wrapped, so looping it inserts blank lines and the height doesn't match anymore
    // so reapply the original string after any siuze change
    internal static float MeasureWrappedHeight(TextNode text, ReadOnlySeString content, float textWidth) {
        var width = Math.Max(40f, textWidth);
        text.Size = new Vector2(width, 2000f);
        text.String = content;
        var height = Math.Max(Constants.GuideBodyFontSize, text.GetTextDrawSize().Y);
        text.Size = new Vector2(width, height);
        text.String = content;
        return height;
    }
}
