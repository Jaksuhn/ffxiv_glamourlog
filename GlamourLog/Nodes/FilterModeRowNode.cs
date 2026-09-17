using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes;

internal sealed class FilterModeRowNode : ResNode {
    private const float RowHeight = 22f;
    private const float LabelWidth = 154f;
    private const float InfoButtonSize = 22f;
    private const float InfoButtonGap = 4f;
    private const float OptionGap = 4f;

    private readonly Func<FilterType> _read;
    private readonly TextNode _label;
    private readonly FilterRadioButtonNode[] _buttons;

    internal FilterModeRowNode(float width, string label, string tooltip, Func<FilterType> read, Action<FilterType> write, System.Action onChanged) {
        _read = read;
        Size = new Vector2(width, RowHeight);

        _label = new TextNode {
            Size = new Vector2(LabelWidth, RowHeight),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.Left,
            TextColor = ColourPalette.HeadingGrey,
            String = label,
            TextFlags = TextFlags.Emboss,
        };
        _label.AttachNode(this);

        var infoButton = new CircleButtonNode {
            Icon = CircleButtonIcon.Exclamation,
            Position = new Vector2(width - InfoButtonSize, 0f),
            Size = new Vector2(InfoButtonSize),
            TextTooltip = tooltip,
        };
        infoButton.AttachNode(this);

        var optionWidth = (width - LabelWidth - InfoButtonGap - InfoButtonSize - OptionGap * 2f) / 3f;
        _buttons = [.. Enum.GetValues<FilterType>()
            .Select((mode, index) => {
                var button = new FilterRadioButtonNode(mode.ToString(), () => {
                    write(mode);
                    onChanged();
                }) {
                    Position = new Vector2(LabelWidth + index * (optionWidth + OptionGap), 0f),
                    Size = new Vector2(optionWidth, RowHeight),
                };
                button.AttachNode(this);
                return button;
            })];

        Sync(inactive: false);
    }

    internal FilterType Value => _read();

    internal void Sync(bool inactive) {
        var selected = _read();
        _label.TextColor = inactive ? ColourPalette.MutedGrey : ColourPalette.Cream;
        for (var index = 0; index < _buttons.Length; index++)
            _buttons[index].Selected = (FilterType)index == selected;
    }

    private sealed class FilterRadioButtonNode : ButtonBase {
        private const float RadioSize = 16f;

        private readonly ResNode _visual;
        private readonly SimpleImageNode _selectedImage;
        private readonly TextNode _label;

        internal FilterRadioButtonNode(string label, System.Action onClick) {
            OnClick = onClick;

            _visual = new ResNode();
            _visual.AttachNode(this);

            var unselectedImage = new SimpleImageNode {
                Position = new Vector2(0f, 3f),
                Size = new Vector2(RadioSize),
                TexturePath = "ui/uld/RadioButtonA.tex",
                TextureCoordinates = Vector2.Zero,
                TextureSize = new Vector2(RadioSize),
                WrapMode = WrapMode.Stretch,
            };
            unselectedImage.AttachNode(_visual);

            _selectedImage = new SimpleImageNode {
                Position = new Vector2(0f, 3f),
                Size = new Vector2(RadioSize),
                TexturePath = "ui/uld/RadioButtonA.tex",
                TextureCoordinates = new Vector2(RadioSize, 0f),
                TextureSize = new Vector2(RadioSize),
                WrapMode = WrapMode.Stretch,
            };
            _selectedImage.AttachNode(_visual);

            _label = new TextNode {
                Position = new Vector2(20f, 0f),
                FontType = FontType.Axis,
                FontSize = 14,
                LineSpacing = 14,
                AlignmentType = AlignmentType.Left,
                TextColor = ColourPalette.Cream,
                String = label,
                TextFlags = TextFlags.Emboss,
            };
            _label.AttachNode(_visual);

            LoadTwoPartTimelines(this, _visual);
            InitializeComponentEvents();
        }

        internal bool Selected {
            set {
                IsChecked = value;
                _selectedImage.IsVisible = value;
            }
        }

        protected override void OnSizeChanged() {
            base.OnSizeChanged();
            _visual.Size = Size;
            _label.Size = new Vector2(Math.Max(0f, Width - 20f), Height);
        }
    }
}
