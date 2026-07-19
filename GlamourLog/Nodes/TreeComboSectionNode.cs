using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes;

public sealed class TreeComboSectionNode : ResNode {
    public NineGridNode BackgroundNode { get; }
    public ImageNode CollapseArrowNode { get; }
    public CollisionNode CollisionNode { get; }
    public TextNode LabelNode { get; }

    public bool IsCollapsed {
        get;
        set {
            if (field == value)
                return;
            field = value;
            CollapseArrowNode.PartId = value ? 0u : 1u;
        }
    }

    public ReadOnlySeString String {
        get => LabelNode.String;
        set => LabelNode.String = value;
    }

    public TreeComboSectionNode(string panelTitle, float listWidth) {
        CollisionNode = new CollisionNode {
            Height = 28f,
            ShowClickableCursor = true,
        };
        CollisionNode.AttachNode(this);

        BackgroundNode = new SimpleNineGridNode {
            TexturePath = "ui/uld/ListItemB.tex",
            TextureSize = new Vector2(48f, 28f),
            TextureCoordinates = new Vector2(0f, 24f),
            Height = 28f,
            TopOffset = 10f,
            LeftOffset = 12f,
            RightOffset = 12f,
            BottomOffset = 12f,
        };
        BackgroundNode.AttachNode(this);

        CollapseArrowNode = new ImageNode {
            Position = new Vector2(0f, 1f),
            Size = new Vector2(24f, 24f),
            PartId = 1,
        };
        CollapseArrowNode.AddPart([
            new Part {
                TexturePath = "ui/uld/ListItemB.tex",
                TextureCoordinates = new Vector2(0f, 0f),
                Size = new Vector2(24f, 24f),
            },
            new Part {
                TexturePath = "ui/uld/ListItemB.tex",
                TextureCoordinates = new Vector2(24f, 0f),
                Size = new Vector2(24f, 24f),
            },
        ]);
        CollapseArrowNode.AttachNode(this);

        LabelNode = new TextNode {
            Position = new Vector2(23f, 0f),
            FontType = FontType.Axis,
            FontSize = 14,
            Height = 28f,
            AlignmentType = AlignmentType.Left,
            TextColor = ColorHelper.GetColor(50),
            TextOutlineColor = ColorHelper.GetColor(7),
        };
        LabelNode.AttachNode(this);

        // after children — Width/Height fire OnSizeChanged
        Width = listWidth;
        Height = 28f;
        String = panelTitle;
        IsCollapsed = false;
    }

    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        BackgroundNode.Width = Width;
        BackgroundNode.Height = 28f;
        CollapseArrowNode.Size = new Vector2(24f, 24f);
        LabelNode.Width = Width - 23f;
        LabelNode.Height = 28f;
        CollisionNode.Width = Width;
        CollisionNode.Height = 28f;
    }
}
