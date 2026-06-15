using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes;
using GlamourLog.Nodes.GuideWindow;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
namespace GlamourLog.Windows.GuideWindow;

public unsafe partial class GuideWindow : NativeAddon {
    public const float WindowWidth = 944f;
    public const float WindowHeight = 600f;

    private const float LeftColumnWidth = 316f;
    private const float ContentPad = 8f;
    private const float ColumnGap = 6f;
    private const float RightColumnHorizontalPad = 40f;
    private const float RightHeaderBodyGap = 14f;
    private const float ScrollContentInset = 16f;
    private const float CategoryHeadingHeight = 24f;
    private const float CategoryHeadingGap = 4f;
    private const float RightHeaderHeight = 68f;

    private static readonly Vector4 CategoryHeadingGrey = new(160f / 255f, 160f / 255f, 160f / 255f, 1f);
    private static readonly Vector4 HeaderTextColor = new(238f / 255f, 225f / 255f, 197f / 255f, 1f);
    private static readonly TextFlags HeaderTextFlags =
        TextFlags.Emboss | TextFlags.WordWrap | TextFlags.MultiLine | unchecked((TextFlags)0x8000);

    private bool _hasPendingScreenOrigin;
    private Vector2 _pendingScreenOrigin;

    private TextNode? _categoryHeading;
    private VerticalListNode? _leftNavList;
    private ResNode? _rightHeaderRow;
    private ScrollingNode<VerticalListNode>? _rightScroll;
    private VerticalLineNode? _splitter;
    private TextNode? _rightTitle;
    private float _rightTextWidth;

    private readonly List<SidebarSection> _categorySections = [];

    private int _expandedCategoryIndex;
    private Page _selectedPage = null!;

    public void OpenOrToggleNear(Vector2 screenTopLeft) {
        if (IsOpen) {
            Close();
            return;
        }

        _pendingScreenOrigin = ClampTopLeft(screenTopLeft);
        _hasPendingScreenOrigin = true;
        Open();
    }

    public void OpenOrToggleCentered() {
        var screen = AtkStage.Instance()->ScreenSize;
        var topLeft = new Vector2(
            (screen.Width - WindowWidth) * 0.5f,
            (screen.Height - WindowHeight) * 0.5f);
        OpenOrToggleNear(topLeft);
    }

    public void CloseIfOpen() {
        if (IsOpen)
            Close();
    }

    public static Vector2 ClampTopLeft(Vector2 origin) {
        var screen = AtkStage.Instance()->ScreenSize;
        var maxX = Math.Max(0f, screen.Width - WindowWidth);
        var maxY = Math.Max(0f, screen.Height - WindowHeight);
        return new Vector2(
            Math.Clamp(origin.X, 0f, maxX),
            Math.Clamp(origin.Y, 0f, maxY));
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        base.OnSetup(addon, atkValueSpan);

        if (_hasPendingScreenOrigin) {
            SetWindowPosition(_pendingScreenOrigin);
            _hasPendingScreenOrigin = false;
        }

        _pageBlocks.Clear();
        foreach (var section in _categorySections) {
            section.CategoryRow.ClearClickHandlers();
            foreach (var (pageRow, _) in section.Pages)
                pageRow.ClearClickHandlers();
        }
        _categorySections.Clear();

        _leftNavList?.Dispose();
        _rightScroll?.Dispose();
        _rightHeaderRow?.Dispose();
        _splitter?.Dispose();
        _categoryHeading?.Dispose();
        _leftNavList = null;
        _rightScroll = null;
        _rightHeaderRow = null;
        _splitter = null;
        _categoryHeading = null;
        _rightTitle = null;

        BuildChrome();

        SyncLeftNav();
        BuildAllRightPanePages();
        PaintRight();
    }

    private void BuildChrome() {
        _selectedPage = NavCategories[0].Pages[0];
        _expandedCategoryIndex = 0;

        var start = ContentStartPosition;
        var size = ContentSize;

        var innerLeft = start.X + ContentPad;
        var innerTop = start.Y + ContentPad;
        var innerBottom = start.Y + size.Y - ContentPad;
        var innerH = innerBottom - innerTop;

        var sepHalf = 1.5f;
        var sepX = innerLeft + LeftColumnWidth + ColumnGap * 0.5f - sepHalf;
        var rightInnerX = innerLeft + LeftColumnWidth + ColumnGap + RightColumnHorizontalPad;
        var rightInnerW = start.X + size.X - ContentPad - rightInnerX - RightColumnHorizontalPad;
        _rightTextWidth = Math.Max(40f, rightInnerW - ScrollContentInset);

        var leftListTop = innerTop + CategoryHeadingHeight + CategoryHeadingGap;
        var leftListH = innerBottom - leftListTop;

        _categoryHeading = new TextNode {
            Position = new Vector2(innerLeft, innerTop),
            Size = new Vector2(LeftColumnWidth, CategoryHeadingHeight),
            FontType = FontType.Jupiter,
            FontSize = 20,
            LineSpacing = 20,
            AlignmentType = AlignmentType.Left,
            TextColor = CategoryHeadingGrey,
            String = "Category",
        };
        _categoryHeading.RemoveTextFlags(TextFlags.Emboss);
        _categoryHeading.AddTextFlags(TextFlags.Emboss);
        _categoryHeading.AttachNode(this);

        // not ScrollingListNode: its scroll collision layer misaligns list button hits
        _leftNavList = new VerticalListNode {
            Position = new Vector2(innerLeft, leftListTop),
            Size = new Vector2(LeftColumnWidth, leftListH),
            ItemSpacing = 0f,
            FitWidth = true,
        };
        _leftNavList.AttachNode(this);

        _splitter = new VerticalLineNode {
            Position = new Vector2(sepX, innerTop),
            Height = innerH,
            Width = 3f,
        };
        _splitter.AttachNode(this);

        _rightHeaderRow = new ResNode {
            Position = new Vector2(rightInnerX, innerTop),
            Size = new Vector2(_rightTextWidth, RightHeaderHeight),
        };
        CreateHeaderPlateNineGrid(new Vector2(_rightTextWidth, RightHeaderHeight)).AttachNode(_rightHeaderRow);
        _rightTitle = new TextNode {
            Size = new Vector2(_rightTextWidth, RightHeaderHeight),
            FontType = FontType.Axis,
            FontSize = 18,
            LineSpacing = 18,
            AlignmentType = AlignmentType.Center,
            TextColor = HeaderTextColor,
            TextFlags = HeaderTextFlags,
        };
        _rightTitle.AttachNode(_rightHeaderRow);
        _rightHeaderRow.AttachNode(this);

        var rightScrollTop = innerTop + RightHeaderHeight + RightHeaderBodyGap;
        var rightScrollHeight = innerBottom - rightScrollTop;
        _rightScroll = SimpleScrollList.Create(
            new Vector2(rightInnerX, rightScrollTop),
            new Vector2(rightInnerW, rightScrollHeight),
            true);
        _rightScroll.ContentNode.ItemSpacing = RightBlockSpacing;
        _rightScroll.AttachNode(this);

        for (var c = 0; c < NavCategories.Length; c++) {
            var catIndex = c;
            var category = NavCategories[c];

            var categoryRow = new SidebarCategoryRowNode(category.Title, () => OnParentCategoryClicked(catIndex));
            _leftNavList.AddNode(categoryRow);

            var pages = new List<(SidebarPageRowNode Btn, Page Page)>();
            foreach (var page in category.Pages) {
                var captured = page;
                var pageRow = new SidebarPageRowNode(page.SubCategoryTitle, () => OnSubClicked(captured));
                _leftNavList.AddNode(pageRow);
                pages.Add((pageRow, page));
            }

            _categorySections.Add(new SidebarSection {
                CategoryRow = categoryRow,
                Pages = pages,
            });
        }

        SyncLeftNav();
        _rightScroll.RecalculateSizes();
    }

    private void SyncLeftNav() {
        for (var i = 0; i < _categorySections.Count; i++) {
            var expanded = i == _expandedCategoryIndex;
            var section = _categorySections[i];
            foreach (var (btn, page) in section.Pages) {
                btn.IsVisible = expanded;
                btn.SetPageSelected(expanded && ReferenceEquals(page, _selectedPage));
            }
        }

        _leftNavList?.RecalculateLayout();
    }

    private void OnParentCategoryClicked(int catIndex) {
        _expandedCategoryIndex = catIndex;
        _selectedPage = NavCategories[catIndex].Pages[0];
        SyncLeftNav();
        PaintRight();
    }

    private void OnSubClicked(Page page) {
        _selectedPage = page;
        SyncLeftNav();
        PaintRight();
    }

    private void PaintRight() {
        if (_rightTitle is null || _rightScroll is null)
            return;

        _rightTitle.String = _selectedPage.SubCategoryTitle;
        ShowRightPanePage(_selectedPage);
        _rightScroll.RecalculateSizes();
        RelayoutVisibleRightPaneBlocks();
        _rightScroll.RecalculateSizes();
        _rightScroll.ScrollBarNode.ScrollPosition = 0;
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        base.OnFinalize(addon);

        foreach (var section in _categorySections) {
            section.CategoryRow.ClearClickHandlers();
            foreach (var (pageRow, _) in section.Pages)
                pageRow.ClearClickHandlers();
        }

        _categorySections.Clear();
        _pageBlocks.Clear();
        _categoryHeading = null;
        _leftNavList = null;
        _rightHeaderRow = null;
        _rightScroll = null;
        _splitter = null;
        _rightTitle = null;
    }

    private static SimpleNineGridNode CreateHeaderPlateNineGrid(Vector2 size) => new() {
        TexturePath = "ui/uld/BgParts.tex",
        TextureCoordinates = new Vector2(1f, 65f),
        TextureSize = new Vector2(32f, 32f),
        LeftOffset = 12f,
        RightOffset = 12f,
        TopOffset = 8f,
        BottomOffset = 8f,
        Size = size,
    };
}
