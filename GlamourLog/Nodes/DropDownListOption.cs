using System.ComponentModel;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal static class DropDownListOption {
    private static readonly TextNode Axis14Measure = new() {
        FontType = FontType.Axis,
        FontSize = 14,
        LineSpacing = 14,
    };

    internal static float OuterWidthForListLabels(IEnumerable<string> labels, float minOuter = 96f) {
        var maxText = 0f;
        foreach (var s in labels) {
            if (string.IsNullOrEmpty(s))
                continue;
            maxText = Math.Max(maxText, Axis14Measure.GetTextDrawSize(s).X);
        }

        // DropDown OptionList width = outer - 8; list row = OptionList - 25; label width = row - 10 -> outer >= text + 43.
        // this still under draws (???), so add a small bit extra
        const float layoutChain = 8f + 25f + 10f;
        const float margin = 10f;
        return Math.Max(minOuter, MathF.Ceiling(maxText) + layoutChain + margin);
    }

    internal static IEnumerable<string> EnumDescriptions<T>() where T : struct, Enum
        => Enum.GetValues<T>().Select(static v => DescriptionForEnumField(v));

    private static string DescriptionForEnumField<T>(T value) where T : struct, Enum {
        var name = Enum.GetName(value);
        if (name is null)
            return value.ToString() ?? string.Empty;
        var field = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Static);
        return field?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? value.ToString() ?? string.Empty;
    }
}
