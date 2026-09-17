using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes;

internal enum CheckboxSelectionState {
    None,
    Partial,
    All,
}

internal sealed class PartialCheckboxNode : CheckboxNode {
    private readonly SimpleImageNode _partialIndicator;

    internal PartialCheckboxNode() {
        _partialIndicator = new SimpleImageNode {
            TexturePath = "ui/uld/FishingNoteBook_hr1.tex",
            TextureCoordinates = new Vector2(2f, 1f),
            TextureSize = new Vector2(22f),
            Size = new Vector2(21f),
            Scale = new Vector2(0.75f),
            Position = new(1, 3),
            WrapMode = WrapMode.Stretch,
            IsVisible = false,
        };
        _partialIndicator.AttachNode(this);
    }

    internal void SetSelectionState(CheckboxSelectionState state) {
        _partialIndicator.IsVisible = state == CheckboxSelectionState.Partial;
        IsChecked = state == CheckboxSelectionState.All;
    }
}
