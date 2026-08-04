using Dalamud.Game.Text.SeStringHandling;

namespace GlamourLog;

internal static class SeStringBuilderExtensions {
    extension(SeStringBuilder sb) {
        public SeStringBuilder Highlight(string text)
            => sb.AddUiForeground(710).Append(text).AddUiForegroundOff();

        public SeStringBuilder Emphasis(string text)
            => sb.AddUiForeground(500).AddUiGlow(501).Append(text).AddUiGlowOff().AddUiForegroundOff();
    }
}
