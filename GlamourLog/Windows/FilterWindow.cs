using AllaganLib.GameSheets.Caches;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes;
using GlamourLog.Services;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Timelines;

namespace GlamourLog;

internal unsafe class FilterWindow : NativeAddon {
    public const float WindowWidth = 560f;
    public const float WindowHeight = 614f;

    private const float ContentPad = 10f;
    private const float SectionIndent = 20f;
    private const float LabelWidth = 154f;
    private const float FilterRowHeight = 22f;

    private readonly List<NodeBase> _nodes = [];
    private readonly List<FilterModeRowNode> _modeRows = [];
    private readonly List<(uint RowId, PartialCheckboxNode Node)> _expansionCheckboxes = [];
    private readonly Dictionary<uint, HashSet<decimal>> _patchesByExpansion = [];
    private FilterDraft? _draft;
    private CheckboxNode? _partialClassJobCheckbox;
    private CheckboxNode? _allExpansionsCheckbox;
    private TextButtonNode? _customPatchesButton;
    private SetListCurrencyFilterNode? _currencyFilter;
    private DropDownNode<SourceFilterOption>? _sourceDropDown;
    private ResNode? _subSourceContainer;
    private ResNode? _subSourceContent;
    private DropDownNode<SourceSubFilterOption>? _subSourceDropDown;
    private TextNode? _subSourceLabel;
    private Vector2 _subSourcePosition;
    private Vector2 _subSourceSize;
    private bool _syncingExpansionControls;
    private bool _committed;
    private bool _hasPendingScreenOrigin;
    private Vector2 _pendingScreenOrigin;

    internal SetListFilterState Filters { get; private set; } = new();

    public void OpenOrToggleNear(Vector2 screenTopLeft) {
        if (IsOpen) {
            Close();
            return;
        }

        _pendingScreenOrigin = ClampFilterWindowTopLeft(screenTopLeft);
        _hasPendingScreenOrigin = true;
        Open();
    }

    public void CloseIfOpen() {
        if (IsOpen)
            Close();
    }

    public static Vector2 ClampFilterWindowTopLeft(Vector2 origin) {
        var screen = AtkStage.Instance()->ScreenSize;
        var maxX = Math.Max(0f, screen.Width - WindowWidth);
        var maxY = Math.Max(0f, screen.Height - WindowHeight);
        return new Vector2(Math.Clamp(origin.X, 0f, maxX), Math.Clamp(origin.Y, 0f, maxY));
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        if (_hasPendingScreenOrigin) {
            SetWindowPosition(_pendingScreenOrigin);
            _hasPendingScreenOrigin = false;
        }

        _nodes.ForEach(node => node.Dispose());
        _nodes.Clear();
        _modeRows.Clear();
        _expansionCheckboxes.Clear();
        _patchesByExpansion.Clear();
        _committed = false;
        _draft = FilterDraft.From(C, Filters);

        var picker = WindowsService.Get().PcSearchSelectClassPicker;
        picker.SetSelectedClassJobIds(Filters.ClassJobIds);
        var start = ContentStartPosition;
        var contentWidth = ContentSize.X - ContentPad * 2f;
        var x = start.X + ContentPad;
        var sectionX = x + SectionIndent;
        var sectionWidth = contentWidth - SectionIndent;
        var y = start.Y + 6f;

        AddSectionTitle(Addon.GetRow(7542).Text.ToString(), x, y, contentWidth);
        y += 28f;

        AddLabel(Addon.GetRow(294).Text.ToString(), sectionX, y, LabelWidth, 36f);
        AddNode(new JobPickerButtonNode(() => {
            if (AddonId != 0)
                picker.Open(checked((ushort)AddonId));
        }) {
            Position = new Vector2(sectionX + LabelWidth, y),
        });
        _partialClassJobCheckbox = AddNode(new CheckboxNode {
            Position = new Vector2(sectionX + LabelWidth + 52f, y + 7f),
            Size = new Vector2(120f, 22f),
            String = Addon.GetRow(3136).Text.ToString(),
            IsChecked = _draft.PartialClassJobMatch,
            OnClick = value => {
                _draft?.PartialClassJobMatch = value;
            },
        });
        _partialClassJobCheckbox.Label.TextColor = ColourPalette.Cream;
        AddNode(new CircleButtonNode {
            Icon = CircleButtonIcon.Exclamation,
            Position = new Vector2(sectionX + LabelWidth + 174f, y + 7f),
            Size = new Vector2(22f),
            TextTooltip = "Whether or not an item should be wearable by only the matching job(s) or if it should contain that job.",
        });
        y += 40f;

        AddLabel("Expansion", sectionX, y, LabelWidth, 24f);
        var expansionX = sectionX + LabelWidth;
        var expansionWidth = sectionWidth - LabelWidth;
        var expansionColumnWidth = sectionWidth / 3f;
        _allExpansionsCheckbox = AddNode(new CheckboxNode {
            Position = new Vector2(expansionX, y),
            Size = new Vector2(expansionWidth - 124f, 22f),
            String = Addon.GetRow(970).Text.ToString(),
            OnClick = _ => {
                if (_draft is null || _syncingExpansionControls)
                    return;
                _draft.ExpansionRowIds.Clear();
                _draft.UseCustomPatches = false;
                SyncExpansionControls();
            },
        });
        _allExpansionsCheckbox.Label.TextColor = ColourPalette.Cream;
        _customPatchesButton = AddNode(new TextButtonNode {
            Position = new Vector2(sectionX + sectionWidth - 116f, y),
            Size = new Vector2(116f, 28f),
            String = Addon.GetRow(1293).Text.ToString(),
            OnClick = ToggleCustomPatches,
        });

        var expansionRows = ExVersion.Where(row => !row.Name.IsEmpty).ToList();
        foreach (var group in CatalogService.Get().GlamourSets.Where(set => set.PatchNo >= 2m).GroupBy(set => (uint)decimal.Truncate(set.PatchNo) - 2)) {
            _patchesByExpansion[group.Key] = [.. group.Select(set => set.PatchNo).Distinct()];
        }
        foreach (var (expansion, index) in expansionRows.Select((row, index) => (row, index))) {
            var column = index % 3;
            var row = index / 3 + 1;
            var checkbox = AddNode(new PartialCheckboxNode {
                Position = new Vector2(sectionX + column * expansionColumnWidth, y + 6f + row * 22f),
                Size = new Vector2(expansionColumnWidth, 22f),
                String = expansion.Name,
                OnClick = value => {
                    if (_draft is null || _syncingExpansionControls)
                        return;
                    if (_draft.UseCustomPatches) {
                        _draft.UseCustomPatches = false;
                        _draft.ExpansionRowIds = [expansion.RowId];
                        SyncExpansionControls();
                        return;
                    }
                    _draft.UseCustomPatches = false;
                    if (value)
                        _draft.ExpansionRowIds.Add(expansion.RowId);
                    else
                        _draft.ExpansionRowIds.Remove(expansion.RowId);
                    SyncExpansionControls();
                },
            });
            checkbox.Label.TextColor = ColourPalette.Cream;
            _expansionCheckboxes.Add((expansion.RowId, checkbox));
        }
        SyncExpansionControls();
        y += 32f + (expansionRows.Count + 2) / 3 * 22f + 4f;

        AddLabel(Addon.GetRow(761).Text.ToString(), sectionX, y, LabelWidth, 24f);
        _currencyFilter = AddNode(new SetListCurrencyFilterNode(sectionWidth - LabelWidth) {
            Position = new Vector2(sectionX + LabelWidth, y),
        });
        var currencyOptions = CatalogService.Get().GetCurrencyFilterItemIds(displayCategoryName: null);
        _currencyFilter.SyncOptions(currencyOptions, _draft.CurrencyItemId);
        _currencyFilter.DropDown.OnOptionSelected = selection => {
            _draft?.CurrencyItemId = selection;
        };
        y += 28f;

        AddLabel(Addon.GetRow(8191).Text.ToString(), sectionX, y, LabelWidth, 24f);
        var sourceOptions = new List<SourceFilterOption> { SourceFilterOption.All };
        sourceOptions.AddRange(CatalogService.Get().GetSourceFilterOptions());
        var selectedSource = sourceOptions.FirstOrDefault(option => option.Type == _draft.Source);
        if (selectedSource == default)
            selectedSource = SourceFilterOption.All;
        _sourceDropDown = AddNode(new DropDownNode<SourceFilterOption> {
            Position = new Vector2(sectionX + LabelWidth, y),
            Size = new Vector2(sectionWidth - LabelWidth, 24f),
            GetLabelFunction = option => option.Label,
            Options = sourceOptions,
            SelectedOption = selectedSource,
            MaxListOptions = 12,
            OnOptionSelected = selection => {
                if (_draft is null)
                    return;
                _draft.Source = selection.Type;
                _draft.SubSource = string.Empty;
                SyncSubSourceOptions();
            },
        });
        y += 28f;

        _subSourceContainer = AddNode(new ResNode {
            Position = new Vector2(sectionX, y),
            Size = new Vector2(sectionWidth, 24f),
        });
        _subSourceContainer.AddTimeline(new TimelineBuilder()
            .BeginFrameSet(1, 19)
            .AddLabel(1, 1, AtkTimelineJumpBehavior.Start, 0)
            .AddLabel(9, 0, AtkTimelineJumpBehavior.PlayOnce, 0)
            .AddLabel(10, 2, AtkTimelineJumpBehavior.Start, 0)
            .AddLabel(19, 0, AtkTimelineJumpBehavior.PlayOnce, 0)
            .EndFrameSet()
            .Build());

        _subSourceContent = new ResNode {
            Size = _subSourceContainer.Size,
        };
        _subSourceContent.AddTimeline(new TimelineBuilder()
            .AddFrameSetWithFrame(1, 9, 1, multiplyColor: new Vector3(100f))
            .AddFrameSetWithFrame(10, 19, 10, multiplyColor: new Vector3(50f))
            .Build());
        _subSourceContent.AttachNode(_subSourceContainer);

        _subSourceLabel = new TextNode {
            Size = new Vector2(LabelWidth, 24f),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.Left,
            TextColor = ColourPalette.Cream,
            String = Addon.GetRow(6625).Text.ToString(),
            TextFlags = TextFlags.Emboss,
        };
        _subSourceLabel.AttachNode(_subSourceContent);
        _subSourcePosition = new Vector2(LabelWidth, 0f);
        _subSourceSize = new Vector2(sectionWidth - LabelWidth, 24f);
        SyncSubSourceOptions();
        y += 32f;

        AddSectionTitle(Addon.GetRow(66).Text.ToString(), x, y, contentWidth);
        y += 28f;

        AddModeRow("Completed", "Whether every obtainable piece in the set is owned.", draft => draft.Completed, (draft, value) => draft.Completed = value);
        AddModeRow("Incompatible", "Whether the set cannot be worn by your character's race and sex.", draft => draft.Incompatible, (draft, value) => draft.Incompatible = value);
        AddModeRow("Unobtainable", "Whether the set cannot currently be obtained.", draft => draft.Unobtainable, (draft, value) => draft.Unobtainable = value);
        AddModeRow("Mogstation", "Whether the set is obtained from the Mogstation.", draft => draft.Mogstation, (draft, value) => draft.Mogstation = value);
        AddModeRow("Contributable", "Whether an inventory piece can currently be contributed to the set.", draft => draft.Contributable, (draft, value) => draft.Contributable = value);
        AddModeRow("Affordable", "Whether you can afford the preferred costs of every missing piece.", draft => draft.Affordable, (draft, value) => draft.Affordable = value);
        AddModeRow("Tradeable", "Whether the set has a piece that can be traded or bought on the market board.", draft => draft.Tradeable, (draft, value) => draft.Tradeable = value);
        AddModeRow("Started", "Whether the set is partially completed.", draft => draft.Started, (draft, value) => draft.Started = value);
        AddModeRow("Armoire", "Whether the set contains a piece that can be stored in the armoire.", draft => draft.Armoire, (draft, value) => draft.Armoire = value);
        AddModeRow("Misplaced", "Whether an armoire-eligible piece is currently stored in the glamour dresser.", draft => draft.Misplaced, (draft, value) => draft.Misplaced = value);
        AddModeRow("Shared model", "Whether another set shares the same complete set of equipment models.", draft => draft.SharedModel, (draft, value) => draft.SharedModel = value);
        SyncModeRows();

        const float buttonWidth = 116f;
        const float buttonHeight = 28f;
        var footerY = start.Y + ContentSize.Y - buttonHeight - 2f;
        AddNode(new HorizontalLineNode {
            Position = new Vector2(x, footerY - 8f),
            Size = new Vector2(contentWidth, 2f),
        });
        AddFooterButton(Addon.GetRow(329).Text.ToString(), x, footerY, ResetDraft);
        AddFooterButton(Addon.GetRow(1218).Text.ToString(), x + contentWidth - buttonWidth * 2f - 8f, footerY, ApplyDraft);
        AddFooterButton(Addon.GetRow(2).Text.ToString(), x + contentWidth - buttonWidth, footerY, CancelDraft);

        void AddModeRow(string label, string tooltip, Func<FilterDraft, FilterType> read, Action<FilterDraft, FilterType> write) {
            var row = AddNode(new FilterModeRowNode(
                sectionWidth,
                label,
                tooltip,
                () => _draft is null ? FilterType.Include : read(_draft),
                value => {
                    if (_draft is not null)
                        write(_draft, value);
                },
                SyncModeRows) {
                Position = new Vector2(sectionX, y),
            });
            _modeRows.Add(row);
            y += FilterRowHeight;
        }

        void AddFooterButton(string label, float buttonX, float buttonY, System.Action onClick) {
            var button = AddNode(new TextButtonNode {
                Position = new Vector2(buttonX, buttonY),
                Size = new Vector2(buttonWidth, buttonHeight),
                String = label,
                OnClick = onClick,
            });
            button.LabelNode.FontType = FontType.Axis;
            button.LabelNode.FontSize = 12;
            button.LabelNode.LineSpacing = 12;
            button.LabelNode.TextColor = ColourPalette.PrimaryWhite;
        }
    }

    private void AddSectionTitle(string title, float x, float y, float width) {
        AddNode(new HorizontalLineNode {
            Position = new Vector2(x, y),
            Size = new Vector2(width, 2f),
        });
        AddNode(new TextNode {
            Position = new Vector2(x + 4f, y + 6f),
            Size = new Vector2(width, 20f),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.TopLeft,
            TextColor = ColourPalette.BodyGrey,
            String = title,
            TextFlags = TextFlags.Emboss,
        });
    }

    private TextNode AddLabel(string label, float x, float y, float width, float height)
        => AddNode(new TextNode {
            Position = new Vector2(x, y),
            Size = new Vector2(width, height),
            FontType = FontType.Axis,
            FontSize = 14,
            LineSpacing = 14,
            AlignmentType = AlignmentType.Left,
            TextColor = ColourPalette.Cream,
            String = label,
            TextFlags = TextFlags.Emboss,
        });

    private T AddNode<T>(T node) where T : NodeBase {
        _nodes.Add(node);
        node.AttachNode(this);
        return node;
    }

    private void SyncSubSourceOptions() {
        if (_draft is null)
            return;

        List<SourceSubFilterOption> options;
        SourceSubFilterOption selected;
        bool enabled;
        if (_draft.Source is null) {
            selected = new SourceSubFilterOption(string.Empty, Addon.GetRow(3134).Text.ToString());
            options = [selected];
            enabled = false;
            _draft.SubSource = string.Empty;
        }
        else {
            var available = CatalogService.Get().GetSubSourceFilterOptions(_draft.Source.Value);
            var all = new SourceSubFilterOption(string.Empty, available.Count == 0 ? Addon.GetRow(1702).Text.ToString() : Addon.GetRow(970).Text.ToString());
            options = [all, .. available];
            enabled = available.Count > 0;

            selected = options.FirstOrDefault(option => option.Key == _draft.SubSource);
            if (selected == default)
                selected = all;
            _draft.SubSource = selected.Key;
        }

        _subSourceDropDown?.Dispose();

        _subSourceDropDown = new DropDownNode<SourceSubFilterOption> {
            Position = _subSourcePosition,
            Size = _subSourceSize,
            MaxListOptions = 16,
            GetLabelFunction = option => option.Label,
            Options = options,
            SelectedOption = selected,
            IsEnabled = enabled,
            OnOptionSelected = selection => {
                _draft?.SubSource = selection.Key;
            },
        };
        _subSourceDropDown.AttachNode(_subSourceContent);
        _subSourceDropDown.CollisionNode.ShowClickableCursor = enabled;
        if (!enabled)
            _subSourceDropDown.CollisionNode.RemoveNodeFlags(NodeFlags.RespondToMouse);
        _subSourceContainer?.Timeline?.PlayAnimation(enabled ? 1 : 2, true);
    }

    private void SyncModeRows() {
        var hasOnlyFilter = _modeRows.Any(row => row.Value == FilterType.Only);
        foreach (var row in _modeRows)
            row.Sync(hasOnlyFilter && row.Value == FilterType.Include);
    }

    private void SyncExpansionControls() {
        if (_draft is null)
            return;
        _syncingExpansionControls = true;
        try {
            _allExpansionsCheckbox!.IsChecked = !_draft.UseCustomPatches && _draft.ExpansionRowIds.Count == 0;
            foreach (var (rowId, checkbox) in _expansionCheckboxes) {
                var state = _draft.UseCustomPatches
                    ? CustomExpansionState(rowId)
                    : _draft.ExpansionRowIds.Contains(rowId)
                        ? CheckboxSelectionState.All
                        : CheckboxSelectionState.None;
                checkbox.SetSelectionState(state);
            }
            _customPatchesButton?.IsChecked = _draft.UseCustomPatches;
        }
        finally {
            _syncingExpansionControls = false;
        }
    }

    private CheckboxSelectionState CustomExpansionState(uint rowId) {
        if (_draft is null || !_patchesByExpansion.TryGetValue(rowId, out var available) || available.Count == 0)
            return CheckboxSelectionState.None;
        var selectedCount = available.Count(_draft.PatchNumbers.Contains);
        if (selectedCount == 0)
            return CheckboxSelectionState.None;
        return selectedCount == available.Count ? CheckboxSelectionState.All : CheckboxSelectionState.Partial;
    }

    private void ToggleCustomPatches() {
        if (_draft is null)
            return;
        var patches = CatalogService.Get().GlamourSets.Select(set => set.PatchNo).Where(patch => patch > 0m).Distinct().OrderBy(patch => patch).ToList();
        WindowsService.Get().PatchPickerWindow.Open(patches, _draft.PatchNumbers, selected => {
            if (_draft is null)
                return;
            _draft.PatchNumbers = selected;
            _draft.UseCustomPatches = true;
            SyncExpansionControls();
        });
    }

    private void ResetDraft() {
        if (_draft is null)
            return;

        _draft.Reset();
        WindowsService.Get().PcSearchSelectClassPicker.SetSelectedClassJobIds([]);
        _partialClassJobCheckbox!.IsChecked = true;
        _currencyFilter!.DropDown.SelectedOption = SetListCurrencyFilterNode.NoneCurrencyId;
        _sourceDropDown!.SelectedOption = SourceFilterOption.All;
        SyncExpansionControls();
        SyncSubSourceOptions();
        SyncModeRows();
    }

    private void ApplyDraft() {
        if (_draft is null)
            return;

        var pickerSelection = WindowsService.Get().PcSearchSelectClassPicker.GetSelectedClassJobIds();
        _draft.ClassJobIds = pickerSelection.Count == 43 ? [] : [.. pickerSelection];
        _draft.ApplyViewsTo(C);
        Filters = _draft.ToFilterState();
        C.Save();
        _committed = true;
        CatalogService.Get().NotifyOwnershipChanged();
        Close();
    }

    private void CancelDraft() {
        WindowsService.Get().PcSearchSelectClassPicker.SetSelectedClassJobIds(Filters.ClassJobIds);
        Close();
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        WindowsService.Get().ClosePatchPickerIfOpen();
        if (!_committed)
            WindowsService.Get().PcSearchSelectClassPicker.SetSelectedClassJobIds(Filters.ClassJobIds);

        // don't dispose nodes here
        _nodes.Clear();
        _modeRows.Clear();
        _draft = null;
        _partialClassJobCheckbox = null;
        _allExpansionsCheckbox = null;
        _customPatchesButton = null;
        _currencyFilter = null;
        _sourceDropDown = null;
        _subSourceContainer = null;
        _subSourceContent = null;
        _subSourceDropDown = null;
        _subSourceLabel = null;
        base.OnFinalize(addon);
    }

    private sealed class FilterDraft {
        internal FilterType Completed { get; set; }
        internal FilterType Incompatible { get; set; }
        internal FilterType Unobtainable { get; set; }
        internal FilterType Mogstation { get; set; }
        internal FilterType Contributable { get; set; }
        internal FilterType Affordable { get; set; }
        internal FilterType Tradeable { get; set; }
        internal FilterType Started { get; set; }
        internal FilterType Armoire { get; set; }
        internal FilterType Misplaced { get; set; }
        internal FilterType SharedModel { get; set; }
        internal List<uint> ClassJobIds { get; set; } = [];
        internal bool PartialClassJobMatch { get; set; } = true;
        internal HashSet<uint> ExpansionRowIds { get; set; } = [];
        internal HashSet<decimal> PatchNumbers { get; set; } = [];
        internal bool UseCustomPatches { get; set; }
        internal ItemInfoType? Source { get; set; }
        internal string SubSource { get; set; } = string.Empty;
        internal uint CurrencyItemId { get; set; }

        internal static FilterDraft From(Configuration config, SetListFilterState filters)
            => new() {
                Completed = config.FilterCompleted,
                Incompatible = config.FilterIncompatible,
                Unobtainable = config.FilterUnobtainable,
                Mogstation = config.FilterMogstation,
                Contributable = config.FilterContributable,
                Affordable = config.FilterAffordable,
                Tradeable = config.FilterTradeable,
                Started = config.FilterStarted,
                Armoire = config.FilterArmoire,
                Misplaced = config.FilterMisplaced,
                SharedModel = config.FilterSharedModels,
                ClassJobIds = [.. filters.ClassJobIds],
                PartialClassJobMatch = filters.PartialClassJobMatch,
                ExpansionRowIds = [.. filters.ExpansionRowIds],
                PatchNumbers = [.. filters.PatchNumbers],
                UseCustomPatches = filters.UseCustomPatches,
                Source = filters.Source,
                SubSource = filters.SubSource,
                CurrencyItemId = filters.CurrencyItemId,
            };

        internal void Reset() {
            Completed = FilterType.Include;
            Incompatible = FilterType.Include;
            Unobtainable = FilterType.Include;
            Mogstation = FilterType.Include;
            Contributable = FilterType.Include;
            Affordable = FilterType.Include;
            Tradeable = FilterType.Include;
            Started = FilterType.Include;
            Armoire = FilterType.Include;
            Misplaced = FilterType.Include;
            SharedModel = FilterType.Include;
            ClassJobIds = [];
            PartialClassJobMatch = true;
            ExpansionRowIds = [];
            PatchNumbers = [];
            UseCustomPatches = false;
            Source = null;
            SubSource = string.Empty;
            CurrencyItemId = 0;
        }

        internal void ApplyViewsTo(Configuration config) {
            config.FilterCompleted = Completed;
            config.FilterIncompatible = Incompatible;
            config.FilterUnobtainable = Unobtainable;
            config.FilterMogstation = Mogstation;
            config.FilterContributable = Contributable;
            config.FilterAffordable = Affordable;
            config.FilterTradeable = Tradeable;
            config.FilterStarted = Started;
            config.FilterArmoire = Armoire;
            config.FilterMisplaced = Misplaced;
            config.FilterSharedModels = SharedModel;
        }

        internal SetListFilterState ToFilterState()
            => new() {
                ClassJobIds = [.. ClassJobIds],
                PartialClassJobMatch = PartialClassJobMatch,
                ExpansionRowIds = [.. ExpansionRowIds],
                PatchNumbers = [.. PatchNumbers],
                UseCustomPatches = UseCustomPatches,
                Source = Source,
                SubSource = SubSource,
                CurrencyItemId = CurrencyItemId,
            };
    }
}
