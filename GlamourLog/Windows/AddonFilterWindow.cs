using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Features.Cabinet;
using GlamourLog.Features.PrismBox;
using GlamourLog.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GlamourLog;

internal sealed record FilterOption(string Label, string Tooltip, Func<Configuration, bool> Read, Action<Configuration> Flip, System.Action? OnChanged = null);

internal enum AddonFilterKind {
    None,
    Armoire,
    Dresser,
}

internal unsafe class AddonFilterWindow : NativeAddon {
    public const float WindowWidth = 420f;

    private const float RowHeight = 20f;
    private const float RowGap = 2f;
    private const float ContentTopPad = 8f;
    private const float OkHeight = 28f;
    private const float OkGap = 8f;
    private const float BottomPad = 10f;
    // native title bar + window chrome outside content
    private const float WindowChromeHeight = 48f;

    private readonly List<(CheckboxNode Node, FilterOption Option)> _checkboxes = [];
    private TextButtonNode? _okButton;
    private bool _hasPendingScreenOrigin;
    private Vector2 _pendingScreenOrigin;
    private FilterOption[] _options = [];

    public AddonFilterKind ActiveKind { get; private set; }

    public static FilterOption[] ArmoireOptions { get; } = [
        new(
            "Hide already deposited items",
            "When the armoire window is open, all entries that already exist inside the armoire will be hidden.",
            c => c.HideCabinetOwnedItems,
            c => c.HideCabinetOwnedItems ^= true,
            () => CabinetListHandler.Get().OnConfigChanged()),
        new(
            "Hide items in gearsets",
            "When the armoire window is open, all entries that are part of gearsets will be hidden",
            c => c.HideCabinetGearsetItems,
            c => c.HideCabinetGearsetItems ^= true,
            () => CabinetListHandler.Get().OnConfigChanged()),
    ];

    public static FilterOption[] DresserOptions { get; } = [
        new(
            "Hide already deposited items",
            "When the glamour creation window is open, items already in the glamour dresser (loose or in an outfit) are hidden.",
            c => c.HideCrystallizeOwnedItems,
            c => c.HideCrystallizeOwnedItems ^= true,
            () => CrystallizeListHandler.Get().OnConfigChanged()),
        new(
            "Hide armoire-eligible items",
            "When the glamour creation window is open, items that can be stored in the armoire are hidden (whether or not you already own them there).",
            c => c.HideCrystallizeArmoireEligibleItems,
            c => c.HideCrystallizeArmoireEligibleItems ^= true,
            () => CrystallizeListHandler.Get().OnConfigChanged()),
        new(
            "Hide non-outfit items",
            "When the glamour creation window is open, items that are not part of any outfit set are hidden.",
            c => c.HideCrystallizeNonOutfitItems,
            c => c.HideCrystallizeNonOutfitItems ^= true,
            () => CrystallizeListHandler.Get().OnConfigChanged()),
    ];

    public static float HeightFor(int optionCount)
        => WindowChromeHeight
           + ContentTopPad
           + optionCount * (RowHeight + RowGap)
           + OkGap
           + OkHeight
           + BottomPad;

    public void OpenOrToggleNear(AddonFilterKind kind, string title, FilterOption[] options, Vector2 screenTopLeft) {
        if (IsOpen && ActiveKind == kind) {
            Close();
            return;
        }

        if (IsOpen)
            Close();

        ActiveKind = kind;
        Title = title;
        _options = options;
        Size = new Vector2(WindowWidth, HeightFor(options.Length));
        _pendingScreenOrigin = ClampTopLeft(screenTopLeft, Size);
        _hasPendingScreenOrigin = true;
        Open();
    }

    public void CloseIfOpen() {
        if (IsOpen)
            Close();
    }

    public static Vector2 ClampTopLeft(Vector2 origin, Vector2 size) {
        var screen = AtkStage.Instance()->ScreenSize;
        var maxX = Math.Max(0f, screen.Width - size.X);
        var maxY = Math.Max(0f, screen.Height - size.Y);
        return new Vector2(Math.Clamp(origin.X, 0f, maxX), Math.Clamp(origin.Y, 0f, maxY));
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        SetWindowSize(Size);

        if (_hasPendingScreenOrigin) {
            SetWindowPosition(_pendingScreenOrigin);
            _hasPendingScreenOrigin = false;
        }

        foreach (var (node, _) in _checkboxes)
            node.Dispose();
        _checkboxes.Clear();
        _okButton?.Dispose();
        _okButton = null;

        var start = ContentStartPosition;
        var rowWidth = ContentSize.X - 16f;
        var x = start.X + 8f;
        var y = start.Y + ContentTopPad;

        foreach (var option in _options) {
            CheckboxNode cb = null!;
            cb = new CheckboxNode {
                Position = new Vector2(x, y),
                Size = new Vector2(rowWidth, RowHeight),
                String = option.Label,
                TextTooltip = option.Tooltip,
                IsChecked = option.Read(C),
                OnClick = _ => {
                    option.Flip(C);
                    cb.IsChecked = option.Read(C);
                    C.Save();
                    option.OnChanged?.Invoke();
                },
            };
            y += RowHeight + RowGap;
            _checkboxes.Add((cb, option));
            cb.AttachNode(this);
        }

        const float okWidth = 150f;
        var okY = Math.Max(y + OkGap, start.Y + ContentSize.Y - OkHeight - BottomPad);
        _okButton = new TextButtonNode {
            Position = new Vector2(x + (rowWidth - okWidth) * 0.5f, okY),
            Size = new Vector2(okWidth, OkHeight),
            String = Addon.GetRow(1).Text,
            OnClick = Close,
        };
        _okButton.LabelNode.FontType = FontType.Axis;
        _okButton.LabelNode.FontSize = 12;
        _okButton.LabelNode.LineSpacing = 12;
        _okButton.LabelNode.TextColor = ColourPalette.PrimaryWhite;
        _okButton.AttachNode(this);
    }

    protected override void OnUpdate(AtkUnitBase* addon) {
        foreach (var (node, option) in _checkboxes)
            node.IsChecked = option.Read(C);

        base.OnUpdate(addon);
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        _checkboxes.Clear();
        _okButton = null;
        ActiveKind = AddonFilterKind.None;
        base.OnFinalize(addon);
    }
}
