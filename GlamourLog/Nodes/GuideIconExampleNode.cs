using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes;

internal sealed class GuideIconExampleNode : ResNode {
    private const uint SampleItemId = 13066;
    private const float SetListIconSize = 29f;
    private static readonly Vector4 TextColor = new(204f / 255f, 204f / 255f, 204f / 255f, 1f);

    private readonly GuideIconExampleKind _kind;
    private readonly ReadOnlySeString _description;
    private readonly float _textBoxHeight;
    private readonly TextNode _text;
    private readonly List<NodeBase> _sampleNodes = [];
    private float _lastLayoutWidth = -1f;

    public GuideIconExampleNode(float width, GuideIconExampleKind kind, ReadOnlySeString description, float? textBoxHeight = null) {
        _kind = kind;
        _description = description;
        _textBoxHeight = textBoxHeight ?? GuideLayout.DefaultGuideIconExampleTextBoxHeight;

        _text = new TextNode {
            FontType = FontType.Axis,
            FontSize = GuideLayout.GuideBodyFontSize,
            LineSpacing = GuideLayout.GuideBodyLineSpacing,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = TextColor,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
        };
        _text.RemoveTextFlags(TextFlags.Emboss);
        _text.AttachNode(this);

        BuildSampleNodes();
        Width = width;
        ApplyHeight(width);
    }

    internal void Relayout(float width) => ApplyHeight(width);

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        if (Width > 1f && Math.Abs(Width - _lastLayoutWidth) > 0.5f)
            ApplyHeight(Width);
    }

    private void BuildSampleNodes() {
        switch (_kind) {
            case GuideIconExampleKind.Checkmark:
                AttachSample(new FramedItemIconNode(SetListIconSize, SampleItemId) {
                    Size = new Vector2(SetListIconSize, SetListIconSize),
                });
                AttachSample(new CheckMarkBadgeNode());
                break;
            case GuideIconExampleKind.FadedDresser:
                AttachSample(CreateStorageBadge(GlamourIconNode.IconPart.DresserFaded));
                break;
            case GuideIconExampleKind.Dresser:
                AttachSample(CreateStorageBadge(GlamourIconNode.IconPart.Dresser));
                break;
            case GuideIconExampleKind.Armoire:
                AttachSample(CreateStorageBadge(GlamourIconNode.IconPart.Armoire));
                break;
            case GuideIconExampleKind.WarningDresser:
                var badge = CreateStorageBadge(GlamourIconNode.IconPart.Dresser);
                AttachSample(badge);
                AttachSample(new ArmoireWarningBadgeNode());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(_kind), _kind, null);
        }
    }

    private static GlamourIconNode CreateStorageBadge(GlamourIconNode.IconPart part)
        => new(part) {
            WrapMode = WrapMode.None,
            Color = Vector4.One,
            MultiplyColor = Vector3.One,
            AddColor = Vector3.Zero,
        };

    private void AttachSample(NodeBase node) {
        node.AttachNode(this);
        _sampleNodes.Add(node);
    }

    private void ApplyHeight(float width) {
        _lastLayoutWidth = width;
        var textX = GuideLayout.IconTextLeft;
        var textW = Math.Max(40f, width - textX);
        var textH = _textBoxHeight;
        var iconH = SampleIconHeight();
        var contentH = Math.Max(textH, iconH);
        var rowH = GuideLayout.RowPadTop + contentH + GuideLayout.RowPadBottom;

        _text.String = _description;
        _text.Position = new Vector2(textX, GuideLayout.RowPadTop + GuideLayout.TextTopInset);
        _text.Size = new Vector2(textW, textH);
        Size = new Vector2(width, rowH);

        LayoutSamples();
    }

    private float SampleIconHeight()
        => _kind switch {
            GuideIconExampleKind.Checkmark => SetListIconSize + GuideLayout.FramedItemFrameBleed,
            _ => _sampleNodes.Count > 0 ? _sampleNodes[0].Size.Y : 12f,
        };

    private void LayoutSamples() {
        if (_sampleNodes.Count == 0)
            return;

        switch (_kind) {
            case GuideIconExampleKind.Checkmark: {
                    var iconX = 0f;
                    // Frame draws above the node origin; offset down so the frame top meets RowPadTop.
                    var iconY = GuideLayout.RowPadTop + GuideLayout.FramedItemFrameBleed;
                    _sampleNodes[0].Position = new Vector2(iconX, iconY);
                    var check = _sampleNodes[1];
                    check.Position = new Vector2(
                        iconX + SetListIconSize - check.Size.X - 4f,
                        iconY + SetListIconSize - check.Size.Y);
                    break;
                }
            default:
                LayoutBadgeSamples(GuideLayout.RowPadTop);
                break;
        }
    }

    private void LayoutBadgeSamples(float iconY) {
        if (_sampleNodes[0] is not GlamourIconNode badge)
            return;

        badge.Position = new Vector2(0f, iconY);

        if (_sampleNodes.Count > 1 && _sampleNodes[1] is ArmoireWarningBadgeNode warn) {
            warn.Position = badge.Position + new Vector2(
                badge.Size.X - warn.Size.X,
                badge.Size.Y - warn.Size.Y);
        }
    }
}
