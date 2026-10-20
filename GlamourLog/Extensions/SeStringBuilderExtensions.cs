using Dalamud.Game.Text.SeStringHandling;
using System.Text.RegularExpressions;

namespace GlamourLog.Extensions;

internal static partial class SeStringBuilderExtensions {
    extension(SeStringBuilder sb) {
        public SeStringBuilder Highlight(string text)
            => sb.AddUiForeground(ThemeColours.Highlight.Key).Append(text).AddUiForegroundOff();

        public SeStringBuilder Emphasis(string text)
            => sb.AddUiForeground(ThemeColours.Emphasis.Key).AddUiGlow(ThemeColours.EmphasisGlow.Key).Append(text).AddUiGlowOff().AddUiForegroundOff();

        public SeStringBuilder Footnote(string text)
            => sb.Emphasis($"※{text}");

        // localized text marks styled runs inline ([h]highlight[/h], [e]emphasis[/e]) so translations can reorder them
        public SeStringBuilder Markup(string text) {
            var last = 0;
            foreach (Match match in MarkupPattern.Matches(text)) {
                if (match.Index > last)
                    sb.Append(text[last..match.Index]);
                if (match.Groups[1].Value == "h")
                    sb.Highlight(match.Groups[2].Value);
                else
                    sb.Emphasis(match.Groups[2].Value);
                last = match.Index + match.Length;
            }
            if (last < text.Length)
                sb.Append(text[last..]);
            return sb;
        }
    }

    private static readonly Regex MarkupPattern = MarkupRegex();

    [GeneratedRegex(@"\[(h|e)\](.*?)\[/\1\]", RegexOptions.Singleline)]
    private static partial Regex MarkupRegex();
}
