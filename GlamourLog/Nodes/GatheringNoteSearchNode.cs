using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes;

public sealed class GatheringNoteSearchNode : ResNode {
    private const float SearchRowHeight = 30f;
    private const float SearchBlockTopLabel = 22f;
    private const float SearchInnerPad = 3f;
    private const float SearchBottomPad = 6f;
    private const float LeftPad = 3f;

    public readonly TextInputNode Input;

    public GatheringNoteSearchNode(float width, Action<ReadOnlySeString> onInputChanged) {
        var searchBlockHeight = SearchBlockTopLabel + 4f + SearchRowHeight + SearchInnerPad + SearchBottomPad;
        Size = new Vector2(width, searchBlockHeight);

        new SimpleNineGridNode {
            TexturePath = "ui/uld/BgParts.tex",
            TextureCoordinates = new Vector2(33f, 37f),
            TextureSize = new Vector2(28f, 28f),
            LeftOffset = 8f,
            RightOffset = 8f,
            TopOffset = 8f,
            BottomOffset = 8f,
            Position = Vector2.Zero,
            Size = new Vector2(width, searchBlockHeight),
        }.AttachNode(this);

        new TextNode {
            Position = new Vector2(6f, 8f),
            Size = new Vector2(width - LeftPad * 2f, SearchBlockTopLabel),
            FontType = FontType.Jupiter,
            FontSize = 20,
            LineSpacing = 20,
            AlignmentType = AlignmentType.Left,
            TextColor = new Vector4(160f / 255f, 160f / 255f, 160f / 255f, 1f),
            String = Addon.GetRow(1470).Text, // Item Search
        }.AttachNode(this);

        var inputPos = new Vector2(LeftPad + SearchInnerPad, SearchBlockTopLabel + 4f + SearchInnerPad);
        var inputSize = new Vector2(width - (LeftPad + SearchInnerPad) * 2f, SearchRowHeight);
        new SimpleNineGridNode {
            TexturePath = "ui/uld/TextInputA.tex",
            TextureCoordinates = new Vector2(24f, 0f),
            TextureSize = new Vector2(24f, 24f),
            LeftOffset = 10f,
            RightOffset = 10f,
            TopOffset = 10f,
            BottomOffset = 10f,
            IsVisible = true,
            Position = inputPos,
            Size = inputSize,
        }.AttachNode(this);

        Input = new TextInputNode {
            Position = inputPos,
            Size = inputSize,
            PlaceholderString = null,
            MaxCharacters = 4096,
            ShowLimitText = false,
            OnInputReceived = onInputChanged,
            OnInputComplete = onInputChanged,
        };
        // nine grid draws the field chrome; stock input fill would double-stack
        Input.BackgroundNode.IsVisible = false;
        Input.FocusBorderNode.IsVisible = true;
        // hide "4096 max" style chrome
        Input.TextLimitsNode.IsVisible = false;
        Input.AttachNode(this);
    }
}
