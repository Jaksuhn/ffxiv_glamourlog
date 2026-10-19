using Dalamud.Plugin;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GlamourLog.Resources.Localization;

/// <summary>
/// Keeps <see cref="UIStrings"/> on the Dalamud UI language and resolves resource names that are only
/// known at runtime (category ids, enum values, ...). Missing translations fall back to the neutral
/// (English) resources. Text is resolved when nodes are built, so a language change applies the next
/// time a window is opened.
/// </summary>
internal static partial class Loc {
    private static IDalamudPluginInterface? _pluginInterface;

    public static void Init(IDalamudPluginInterface pluginInterface) {
        _pluginInterface = pluginInterface;
        pluginInterface.LanguageChanged += SetLanguage;
        SetLanguage(pluginInterface.UiLanguage);
    }

    public static void Dispose() {
        if (_pluginInterface is not null)
            _pluginInterface.LanguageChanged -= SetLanguage;
        _pluginInterface = null;
    }

    /// <summary>True when the UI is in English, i.e. the neutral resources are used as-is.</summary>
    public static bool IsDefault => UIStrings.Culture is not { } culture || culture.TwoLetterISOLanguageName is "en" or "iv";

    /// <summary>Looks up a resource by a runtime-built name, or returns <paramref name="fallback"/> if there is none.</summary>
    public static string GetOr(string name, string fallback)
        => UIStrings.ResourceManager.GetString(ToResourceName(name), UIStrings.Culture) ?? fallback;

    public static string Get(string name) => GetOr(name, name);

    /// <summary>Display name for an internal (English) category id.</summary>
    public static string Category(string categoryId) => GetOr($"Category_{categoryId}", categoryId);

    private static void SetLanguage(string langCode) {
        try {
            UIStrings.Culture = CultureInfo.GetCultureInfo(langCode);
        } catch (CultureNotFoundException) {
            UIStrings.Culture = CultureInfo.InvariantCulture;
        }
    }

    // resource names only hold identifier characters ("V&C Dungeons" -> "V_C_Dungeons")
    private static string ToResourceName(string name) => InvalidNameChars().Replace(name, "_");

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex InvalidNameChars();
}
