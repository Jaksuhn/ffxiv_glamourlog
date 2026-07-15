using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal sealed unsafe class LogWindowCategoryColumnNode : ResNode {
    private const float CategoryHeadingHeight = 26f;
    private const float CategoryCountWidth = 44f;
    private const float CategoryCountRightInset = 4f;
    private static readonly Vector4 GatheringHeadingGrey = ColourPalette.HeadingGrey;
    private static readonly Vector4 CategoryNameGold = ColourPalette.CategoryGold;

    private readonly List<ListButtonNode> buttons = [];
    private readonly Dictionary<ListButtonNode, TextNode> countByButton = [];
    private readonly Dictionary<ListButtonNode, string> buttonCategoryMap = [];
    private readonly Action<string> onCategorySelected;

    public GatheringNoteSearchNode Search { get; }
    public ScrollingNode<VerticalListNode> CategoryList { get; }

    public LogWindowCategoryColumnNode(float width, float columnHeight, System.Action onSearchChanged, Action<string> onCategorySelected) {
        this.onCategorySelected = onCategorySelected;

        const float topGap = 2f;
        Size = new Vector2(width, columnHeight);

        Search = new GatheringNoteSearchNode(width, _ => onSearchChanged()) {
            Position = new Vector2(0f, topGap),
        };
        Search.AttachNode(this);

        var afterSearchY = topGap + Search.Size.Y + 6f;
        var heading = new TextNode {
            Position = new Vector2(3f, afterSearchY),
            Size = new Vector2(width - 6f, CategoryHeadingHeight),
            FontType = FontType.Jupiter,
            FontSize = 23,
            LineSpacing = 23,
            AlignmentType = AlignmentType.BottomLeft,
            TextColor = GatheringHeadingGrey,
            String = Addon.GetRow(1485).Text,
        };
        heading.RemoveTextFlags(TextFlags.Emboss);
        heading.AddTextFlags(TextFlags.Emboss);
        heading.AttachNode(this);

        var listY = afterSearchY + CategoryHeadingHeight + 2f;
        var listHeight = Math.Max(0f, columnHeight - listY);
        CategoryList = SimpleScrollList.Create(new Vector2(0f, listY), new Vector2(width, listHeight), true);
        CategoryList.AttachNode(this);
    }

    public void RebuildFromPaneOrder(IReadOnlyList<string> paneOrder, string selectedCategoryId) {
        CategoryList.ContentNode.Clear();
        buttons.Clear();
        buttonCategoryMap.Clear();
        countByButton.Clear();

        foreach (var categoryId in paneOrder) {
            var captured = categoryId;
            var buttonWidth = Math.Max(0f, CategoryList.ContentNode.Width);
            var button = new ListButtonNode {
                Size = new Vector2(buttonWidth, 24f),
                String = string.Empty,
                Selected = captured == selectedCategoryId,
            };
            button.LabelNode.Position = new Vector2(4f, 1f);
            button.LabelNode.Size = new Vector2(button.Width - CategoryCountWidth - CategoryCountRightInset - 8f, button.Height - 2f);
            button.LabelNode.FontType = FontType.Jupiter;
            button.LabelNode.FontSize = 20;
            button.LabelNode.LineSpacing = 20;
            button.LabelNode.AlignmentType = AlignmentType.Left;
            button.LabelNode.TextColor = CategoryNameGold;
            button.LabelNode.String = categoryId;
            button.LabelNode.AddTextFlags(TextFlags.Emboss, TextFlags.Ellipsis);

            var countNode = new TextNode {
                Position = new Vector2(button.Width - CategoryCountWidth - CategoryCountRightInset, 1f),
                Size = new Vector2(CategoryCountWidth, button.Height - 2f),
                FontType = FontType.Axis,
                FontSize = 11,
                LineSpacing = 11,
                AlignmentType = AlignmentType.BottomRight,
                TextColor = GatheringHeadingGrey,
            };
            countNode.AttachNode(button);

            button.AddEvent(AtkEventType.MouseClick, (_, _, _, _, atkEventData) => {
                if (atkEventData == null)
                    return;
                ref var eventData = ref *atkEventData;
                if (!eventData.IsLeftClick)
                    return;
                onCategorySelected(captured);
            });

            buttons.Add(button);
            buttonCategoryMap[button] = categoryId;
            countByButton[button] = countNode;
            CategoryList.ContentNode.AddNode(button);
        }

        CategoryList.RecalculateSizes();
        SyncCountLayouts();
        ResetScrollToTop();
    }

    public void UpdateButtonStates(string selectedCategoryId, Func<string, List<GlamourSet>> categoryRows, OwnershipQuery q) {
        foreach (var btn in buttons) {
            if (!buttonCategoryMap.TryGetValue(btn, out var categoryId))
                continue;

            btn.LabelNode.String = categoryId;
            btn.Selected = categoryId == selectedCategoryId;
            if (countByButton.TryGetValue(btn, out var countNode)) {
                var cr = categoryRows(categoryId);
                countNode.String = $"{q.CountCompleteIn(cr)}/{cr.Count}";
            }
        }
        SyncCountLayouts();
    }

    public void SyncCountLayouts() {
        var buttonWidth = Math.Max(0f, CategoryList.ContentNode.Width);
        foreach (var btn in buttons) {
            if (Math.Abs(btn.Width - buttonWidth) > 0.5f) {
                btn.Width = buttonWidth;
                btn.LabelNode.Size = new Vector2(buttonWidth - CategoryCountWidth - CategoryCountRightInset - 8f, btn.Height - 2f);
            }

            if (countByButton.TryGetValue(btn, out var count)) {
                count.Position = new Vector2(btn.Width - CategoryCountWidth - CategoryCountRightInset, 1f);
                count.Size = new Vector2(CategoryCountWidth, btn.Height - 2f);
                count.AlignmentType = AlignmentType.BottomRight;
            }
        }
    }

    public void ResetScrollToTop() {
        CategoryList.ScrollBarNode.ScrollPosition = 0;
        CategoryList.RecalculateSizes();
    }

    public void PrepareForClose() {
        CategoryList.ScrollBarNode.OnValueChanged = null;
        var bar = (AtkComponentScrollBar*)CategoryList.ScrollBarNode;
        bar->IsBeingDragged = false;
        bar->SetContentNode(null, null);
        bar->SetScrollPosition(0);
    }
}
