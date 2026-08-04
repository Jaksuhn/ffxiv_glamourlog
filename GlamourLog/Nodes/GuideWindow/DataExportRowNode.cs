using System.ComponentModel;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes.GuideWindow;

internal sealed class DataExportRowNode : ResNode {
    private const float RowHeight = 28f;
    private const float ButtonWidth = 150f;
    private const float ButtonGap = 12f;
    private const float LabelHeight = 20f;

    private readonly TextNode _label;
    private readonly TextButtonNode _copyButton;

    public DataExportRowNode(float width, GlamourDataExportFormat format, System.Action onCopy) {
        _label = new TextNode {
            Position = new Vector2(0f, (RowHeight - LabelHeight) * 0.5f),
            Size = new Vector2(LabelWidth(width), LabelHeight),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.Left,
            TextColor = ColourPalette.Cream,
            String = FormatLabel(format),
            TextFlags = TextFlags.Emboss,
        };
        _label.AttachNode(this);

        _copyButton = new TextButtonNode {
            Position = new Vector2(width - ButtonWidth, 0f),
            Size = new Vector2(ButtonWidth, RowHeight),
            String = "Copy to clipboard",
            OnClick = onCopy,
        };
        _copyButton.LabelNode.FontType = FontType.Axis;
        _copyButton.LabelNode.FontSize = 12;
        _copyButton.LabelNode.LineSpacing = 12;
        _copyButton.LabelNode.TextColor = ColourPalette.PrimaryWhite;
        _copyButton.AttachNode(this);

        Size = new Vector2(width, RowHeight);
    }

    internal void Relayout(float width) {
        _label.Size = new Vector2(LabelWidth(width), LabelHeight);
        _copyButton.Position = new Vector2(width - ButtonWidth, 0f);
        Size = new Vector2(width, RowHeight);
    }

    private static float LabelWidth(float width)
        => Math.Max(40f, width - ButtonWidth - ButtonGap);

    private static string FormatLabel(GlamourDataExportFormat format) {
        var name = Enum.GetName(format);
        if (name is null)
            return format.ToString();
        var field = typeof(GlamourDataExportFormat).GetField(name, BindingFlags.Public | BindingFlags.Static);
        return field?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name;
    }
}
