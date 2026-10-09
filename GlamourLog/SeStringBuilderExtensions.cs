using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog;

internal static class SeStringBuilderExtensions {
    extension(SeStringBuilder sb) {
        public SeStringBuilder Highlight(string text)
            => sb.AddUiForeground(ThemeColours.Highlight.Key).Append(text).AddUiForegroundOff();

        public SeStringBuilder Emphasis(string text)
            => sb.AddUiForeground(ThemeColours.Emphasis.Key).AddUiGlow(ThemeColours.EmphasisGlow.Key).Append(text).AddUiGlowOff().AddUiForegroundOff();

        public SeStringBuilder Footnote(string text)
            => sb.Emphasis($"※{text}");
    }
}
