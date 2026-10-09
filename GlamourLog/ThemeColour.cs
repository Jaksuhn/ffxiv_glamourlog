using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace GlamourLog;

internal readonly record struct ThemeColour(uint RowId) {
    public ushort Key => checked((ushort)RowId);

    public Vector4 Current => ColorHelper.GetColor(RowId);

    public static ThemeColour Row(uint rowId) => new(rowId);
}

internal static unsafe class ThemeColours {
    public static readonly ThemeColour Text = new(1); // white on dark, dark brown on light
    public static readonly ThemeColour White = new(50); // pure white all themes
    public static readonly ThemeColour Header = new(50); // white on dark, pure white on light
    public static readonly ThemeColour Counter = new(64);
    public static readonly ThemeColour CounterEdge = new(65);
    public static readonly ThemeColour Standard = new(8); // cream on dark, brown on light
    public static readonly ThemeColour Body = new(2);
    public static readonly ThemeColour Heading = new(3);
    public static readonly ThemeColour Muted = new(4);
    public static readonly ThemeColour Gold = new(22);
    public static readonly ThemeColour Subtitle = new(23); // brown
    public static readonly ThemeColour GoldEdge = new(28);
    public static readonly ThemeColour Ink = new(51); // black
    public static ThemeColour SeparatorLabel => ReadTheme() == 1 ? White : Ink; // TODO: find a better UIColor
    public static readonly ThemeColour Highlight = new(802); // amber, TODO: find a better UIColor
    public static readonly ThemeColour Emphasis = new(500);

    public static readonly ThemeColour EmphasisGlow = new(507);
    public static readonly ThemeColour LinkedName = new(549);
    public static readonly ThemeColour LinkedNameGlow = new(550);
    public static bool UseEmboss => ReadTheme() == 0; // I think?

    public static void ApplyEmboss(TextNode node) {
        if (UseEmboss)
            node.AddTextFlags(TextFlags.Emboss);
        else
            node.RemoveTextFlags(TextFlags.Emboss);
    }

    private static byte ReadTheme() {
        var stage = AtkStage.Instance();
        if (stage is null || stage->AtkUIColorHolder is null)
            return 0;

        return stage->AtkUIColorHolder->ActiveColorThemeType;
    }
}
