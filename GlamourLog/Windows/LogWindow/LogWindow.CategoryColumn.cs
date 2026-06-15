using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;

namespace GlamourLog;

internal unsafe partial class LogWindow {
    private const float CategoryCountWidth = 44f;
    private const float CategoryCountRightInset = 4f;

    private List<string> BuildOrderedCategoryPaneList() {
        var r = new List<string> { AllCategoryId, Svc.Get<CatalogService>().UncategorizedTab.Name };
        foreach (var (category, _) in Svc.Get<CatalogService>().OutfitCategories.Select((c, ix) => (c, ix)).OrderBy(x => x.c.UiPriority).ThenBy(x => x.ix))
            r.Add(category.Name);
        r.Add(Svc.Get<CatalogService>().MiscArmoireTab.Name);
        r.Add(Svc.Get<CatalogService>().UnobtainableTab.Name);
        return r;
    }

    private void BuildCategoryButtons() {
        if (_categoryListNode is null)
            return;
        if (_categoryButtons.Count > 0)
            return;
        RebuildCategoryButtonsFromPaneOrder();
    }

    // rebuilds left pane from _categoryPaneOrder (first setup + when CatalogService.DataVersion bumps)
    private void RebuildCategoryButtonsFromPaneOrder() {
        if (_categoryListNode is null)
            return;

        // full clear is ok here (small list); hot-path set/detail lists avoid ktk Clear for dispose/scrollbar races
        _categoryListNode.ContentNode.Clear();
        _categoryButtons.Clear();
        _categoryButtonMap.Clear();
        _categoryCountByButton.Clear();

        foreach (var categoryId in _categoryPaneOrder) {
            var captured = categoryId;
            var buttonWidth = Math.Max(0f, _categoryListNode.ContentNode.Width);
            var button = new ListButtonNode {
                Size = new Vector2(buttonWidth, 24f),
                String = string.Empty,
                Selected = captured == _selectedCategoryId,
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

            // native atk click path (not imgui); keeps category switch inside ktk addon event flow
            button.AddEvent(AtkEventType.MouseClick, (_, _, _, _, atkEventData) => {
                if (atkEventData == null)
                    return;
                ref var eventData = ref *atkEventData;
                if (!eventData.IsLeftClick)
                    return;
                if (_selectedCategoryId == captured)
                    return;
                _selectedCategoryId = captured;
                _selectedSet = null;
                _sourceFilterPieceItemId = null;
                _pendingClearSetSelection = true;
                _pendingRefreshListsAndDetails = true;
                _pendingResetSetScroll = true;
                _pendingResetDetailScroll = true;
            });
            _categoryButtons.Add(button);
            _categoryButtonMap[button] = categoryId;
            _categoryCountByButton[button] = countNode;
            _categoryListNode.ContentNode.AddNode(button);
        }

        _categoryListNode.RecalculateSizes();
        SyncCategoryCountLayouts();
        ResetScrollToTop(_categoryListNode);
    }

    private void SyncCategoryCountLayouts() {
        if (_categoryListNode is null)
            return;

        var buttonWidth = Math.Max(0f, _categoryListNode.ContentNode.Width);
        foreach (var btn in _categoryButtons) {
            if (Math.Abs(btn.Width - buttonWidth) > 0.5f) {
                btn.Width = buttonWidth;
                btn.LabelNode.Size = new Vector2(buttonWidth - CategoryCountWidth - CategoryCountRightInset - 8f, btn.Height - 2f);
            }

            if (_categoryCountByButton.TryGetValue(btn, out var count)) {
                count.Position = new Vector2(btn.Width - CategoryCountWidth - CategoryCountRightInset, 1f);
                count.Size = new Vector2(CategoryCountWidth, btn.Height - 2f);
                count.AlignmentType = AlignmentType.BottomRight;
            }
        }
    }

    private List<GlamourSet> CategoryRows(string categoryId)
        => categoryId == AllCategoryId
            ? [.. Svc.Get<CatalogService>().GlamourSets]
            : Svc.Get<CatalogService>().GlamourSetsByCategory.TryGetValue(categoryId, out var list) ? list : [];

    private void SyncCategoryPaneToDataVersion() {
        if (_categoryListNode is null)
            return;

        var catalog = Svc.Get<CatalogService>();
        var dataVersion = catalog.DataVersion;
        if (_lastDataVersion == dataVersion)
            return;

        _lastDataVersion = dataVersion;

        var newOrder = BuildOrderedCategoryPaneList();
        if (CategoryPaneOrderMatches(newOrder))
            return;

        _categoryPaneOrder.Clear();
        _categoryPaneOrder.AddRange(newOrder);
        _pendingCategoryPaneRebuild = true;
        if (!_categoryPaneOrder.Contains(_selectedCategoryId))
            _selectedCategoryId = catalog.UncategorizedTab.Name;
        ResetScrollToTop(_setListNode);
    }

    private bool CategoryPaneOrderMatches(List<string> newOrder) {
        if (_categoryPaneOrder.Count != newOrder.Count)
            return false;
        for (var i = 0; i < newOrder.Count; i++) {
            if (_categoryPaneOrder[i] != newOrder[i])
                return false;
        }
        return true;
    }
}
