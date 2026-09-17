using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GlamourLog.Windows;

internal sealed unsafe class PatchPickerWindow : NativeAddon {
    internal const float WindowWidth = 560f;
    internal const float WindowHeight = 360f;

    private const float PaneGap = 12f;
    private const float LeftPaneWidth = 190f;
    private const float RowHeight = 26f;

    private readonly List<NodeBase> _nodes = [];
    private readonly List<NodeBase> _detailNodes = [];
    private readonly List<PatchGroupControls> _groups = [];
    private IReadOnlyList<decimal> _patches = [];
    private HashSet<decimal> _selection = [];
    private Action<HashSet<decimal>>? _onApply;
    private bool _syncingCheckboxes;

    internal void Open(IReadOnlyList<decimal> patches, IReadOnlyCollection<decimal> selected, Action<HashSet<decimal>> onApply) {
        _patches = patches;
        _selection = selected.Count == 0 ? [.. patches] : [.. selected];
        _onApply = onApply;
        Open();
    }

    internal void CloseIfOpen() {
        if (IsOpen)
            Close();
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        _nodes.ForEach(node => node.Dispose());
        _nodes.Clear();
        _detailNodes.Clear();
        _groups.Clear();

        var start = ContentStartPosition;
        var contentWidth = ContentSize.X - 20f;
        var x = start.X + 10f;
        var y = start.Y + 8f;
        var rightX = x + LeftPaneWidth + PaneGap;
        var rightWidth = contentWidth - LeftPaneWidth - PaneGap;
        var expansionNames = IDataManager.Get().GetSheet<ExVersion>().ToDictionary(row => row.RowId, row => row.Name.ToString());
        var patchGroups = _patches
            .Where(patch => patch >= 2m)
            .GroupBy(patch => (uint)decimal.Truncate(patch) - 2)
            .OrderBy(group => group.Key)
            .Select(group => (RowId: group.Key, Patches: group.OrderBy(patch => patch).ToList()))
            .ToList();

        foreach (var (group, index) in patchGroups.Select((group, index) => (group, index))) {
            PatchGroupControls controls = null!;
            var rowY = y + index * RowHeight;
            var parent = AddNode(new PartialCheckboxNode {
                Position = new Vector2(x, rowY + 2f),
                Size = new Vector2(22f),
                String = string.Empty,
                OnClick = value => SetGroupSelection(controls, value),
            });
            var selector = AddNode(new ListButtonNode {
                Position = new Vector2(x + 24f, rowY),
                Size = new Vector2(LeftPaneWidth - 24f, 24f),
                String = expansionNames.GetValueOrDefault(group.RowId, $"Expansion {group.RowId + 1}"),
                OnClick = () => SelectGroup(controls, rightX, y, rightWidth),
            });
            controls = new PatchGroupControls(group.RowId, parent, selector, group.Patches);
            _groups.Add(controls);
            SyncGroupParent(controls);
        }

        AddNode(new VerticalLineNode {
            Position = new Vector2(x + LeftPaneWidth + PaneGap * 0.5f - 1.5f, y),
            Width = 3f,
            Height = ContentSize.Y - 84f,
        });

        if (_groups.FirstOrDefault() is { } first)
            SelectGroup(first, rightX, y, rightWidth);

        const float buttonWidth = 134f;
        const float buttonHeight = 28f;
        var footerY = start.Y + ContentSize.Y - buttonHeight - 2f;
        AddNode(new TextNode {
            Position = new Vector2(x + 4f, footerY - 42f),
            Size = new Vector2(contentWidth - 8f, 32f),
            FontType = FontType.Axis,
            FontSize = 12,
            LineSpacing = 12,
            TextColor = ColourPalette.Cream,
            String = new Lumina.Text.ReadOnly.ReadOnlySeString(new SeStringBuilder().Footnote("Patches are for when an item was added to the game, not necessarily when it was made available.").Encode()),
            TextFlags = TextFlags.Emboss | TextFlags.WordWrap | TextFlags.MultiLine,
        });
        AddNode(new HorizontalLineNode {
            Position = new Vector2(x, footerY - 8f),
            Size = new Vector2(contentWidth, 2f),
        });
        AddNode(new TextButtonNode {
            Position = new Vector2(x + (contentWidth - buttonWidth) * 0.5f, footerY),
            Size = new Vector2(buttonWidth, buttonHeight),
            String = "Confirm",
            OnClick = () => {
                _onApply?.Invoke([.. _selection]);
                Close();
            },
        });
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        _nodes.Clear();
        _detailNodes.Clear();
        _groups.Clear();
        _onApply = null;
        base.OnFinalize(addon);
    }

    private void SelectGroup(PatchGroupControls group, float x, float y, float width) {
        foreach (var candidate in _groups)
            candidate.Selector.Selected = ReferenceEquals(candidate, group);

        foreach (var node in _detailNodes) {
            _nodes.Remove(node);
            node.Dispose();
        }
        _detailNodes.Clear();

        const int columns = 2;
        var columnWidth = width / columns;
        foreach (var (patch, index) in group.Patches.Select((patch, index) => (patch, index))) {
            AddDetailNode(new CheckboxNode {
                Position = new Vector2(x + index % columns * columnWidth, y + index / columns * 24f),
                Size = new Vector2(columnWidth, 24f),
                String = $"Patch {patch:0.0#}",
                IsChecked = _selection.Contains(patch),
                OnClick = value => {
                    if (_syncingCheckboxes)
                        return;
                    if (value)
                        _selection.Add(patch);
                    else
                        _selection.Remove(patch);
                    SyncGroupParent(group);
                },
            });
        }
    }

    private void SetGroupSelection(PatchGroupControls group, bool selected) {
        if (_syncingCheckboxes)
            return;
        foreach (var patch in group.Patches) {
            if (selected)
                _selection.Add(patch);
            else
                _selection.Remove(patch);
        }
        SelectGroup(group, ContentStartPosition.X + 10f + LeftPaneWidth + PaneGap, ContentStartPosition.Y + 8f,
            ContentSize.X - 20f - LeftPaneWidth - PaneGap);
        SyncGroupParent(group);
    }

    private void SyncGroupParent(PatchGroupControls group) {
        _syncingCheckboxes = true;
        try {
            var selectedCount = group.Patches.Count(_selection.Contains);
            group.Parent.SetSelectionState(selectedCount switch {
                0 => CheckboxSelectionState.None,
                var count when count == group.Patches.Count => CheckboxSelectionState.All,
                _ => CheckboxSelectionState.Partial,
            });
        }
        finally {
            _syncingCheckboxes = false;
        }
    }

    private T AddNode<T>(T node) where T : NodeBase {
        _nodes.Add(node);
        node.AttachNode(this);
        return node;
    }

    private T AddDetailNode<T>(T node) where T : NodeBase {
        _detailNodes.Add(AddNode(node));
        return node;
    }

    private sealed record PatchGroupControls(
        uint RowId,
        PartialCheckboxNode Parent,
        ListButtonNode Selector,
        IReadOnlyList<decimal> Patches);
}
