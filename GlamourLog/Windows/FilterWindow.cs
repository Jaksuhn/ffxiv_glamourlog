using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Services;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace GlamourLog;

internal unsafe class FilterWindow : NativeAddon {
    public const float WindowWidth = 456f;
    public const float WindowHeight = 314f;
    private readonly List<CheckboxNode> _checkboxes = [];
    private TextButtonNode? _okButton;
    private bool _hasPendingScreenOrigin;
    private Vector2 _pendingScreenOrigin;

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
        return new Vector2(
            Math.Clamp(origin.X, 0f, maxX),
            Math.Clamp(origin.Y, 0f, maxY));
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan) {
        if (_hasPendingScreenOrigin) {
            SetWindowPosition(_pendingScreenOrigin);
            _hasPendingScreenOrigin = false;
        }

        _checkboxes.ForEach(c => c.Dispose());
        _checkboxes.Clear();
        _okButton?.Dispose();
        _okButton = null;

        var start = ContentStartPosition;
        var rowWidth = ContentSize.X - 16f;
        var x = start.X + 8f;
        var y = start.Y + 8f;
        const float rowHeight = 20f;

        void AddCheckbox(string label, string tooltip, Func<Configuration, bool> read, Action<Configuration> flip) {
            CheckboxNode cb = null!;
            cb = new CheckboxNode {
                Position = new Vector2(x, y),
                Size = new Vector2(rowWidth, rowHeight),
                String = label,
                TextTooltip = tooltip,
                IsChecked = read(C),
                OnClick = _ => {
                    flip(C);
                    cb.IsChecked = read(C);
                    C.Save();
                    Svc.Get<CatalogService>().NotifyOwnershipChanged();
                },
            };
            y += rowHeight + 2f;
            _checkboxes.Add(cb);
            cb.AttachNode(this);
        }

        AddCheckbox(
            "Hide completed",
            "Hide sets where every piece is owned",
            c => c.HideCompleted,
            c => c.HideCompleted ^= true);
        AddCheckbox(
            "Hide incompatible items",
            "Hides all sets whose items are unwearable due to race or sex restrictions",
            c => c.HideIncompatible,
            c => c.HideIncompatible ^= true);
        AddCheckbox(
            "Hide uncontributable",
            "Hide sets where no piece is in your inventory to contribute to the set",
            c => c.HideUnready,
            c => c.HideUnready ^= true);
        AddCheckbox(
            "Hide shared models",
            "Hide outfits that share the same models. Will still show any sets that are started or completed.",
            c => c.HideSharedModels,
            c => c.HideSharedModels ^= true);
        AddCheckbox(
            "Show only completed",
            "Show only sets where every piece is owned",
            c => c.ShowOnlyCompleted,
            c => c.ShowOnlyCompleted ^= true);
        AddCheckbox(
            "Show only affordable sets",
            "Show only sets where you can afford the currency cost of all pieces",
            c => c.HideUnaffordable,
            c => c.HideUnaffordable ^= true);
        AddCheckbox(
            "Show only tradeable",
            "Show only sets whose pieces can be bought on the marketboard or traded",
            c => c.HideNoMarketboard,
            c => c.HideNoMarketboard ^= true);
        AddCheckbox(
            "Show only started",
            "Show only sets that are partially completed",
            c => c.HideNonPartials,
            c => c.HideNonPartials ^= true);
        AddCheckbox(
            "Show only misplaced",
            "Show only sets that have pieces in the dresser that could be stored in the armoire",
            c => c.ShowOnlyMisplaced,
            c => c.ShowOnlyMisplaced ^= true);

        const float okWidth = 150f;
        const float okHeight = 28f;
        var bottomPad = 10f;
        var checkboxCount = 9;
        var checklistBottom = start.Y + 8f + checkboxCount * (rowHeight + 2f);
        var okY = start.Y + ContentSize.Y - okHeight - bottomPad;
        var minOkY = checklistBottom + 8f;
        if (okY < minOkY)
            okY = minOkY;

        var okX = x + (rowWidth - okWidth) * 0.5f;

        _okButton = new TextButtonNode {
            Position = new Vector2(okX, okY),
            Size = new Vector2(okWidth, okHeight),
            String = Addon.GetRow(1).Text,
            OnClick = Close,
        };
        _okButton.LabelNode.FontType = FontType.Axis;
        _okButton.LabelNode.FontSize = 12;
        _okButton.LabelNode.LineSpacing = 12;
        _okButton.LabelNode.TextColor = ImGuiColors.DalamudWhite;
        _okButton.AttachNode(this);
    }

    protected override void OnUpdate(AtkUnitBase* addon) {
        if (_checkboxes.Count < 9) {
            base.OnUpdate(addon);
            return;
        }

        _checkboxes[0].IsChecked = C.HideCompleted;
        _checkboxes[1].IsChecked = C.HideIncompatible;
        _checkboxes[2].IsChecked = C.HideUnready;
        _checkboxes[3].IsChecked = C.HideSharedModels;
        _checkboxes[4].IsChecked = C.ShowOnlyCompleted;
        _checkboxes[5].IsChecked = C.HideUnaffordable;
        _checkboxes[6].IsChecked = C.HideNoMarketboard;
        _checkboxes[7].IsChecked = C.HideNonPartials;
        _checkboxes[8].IsChecked = C.ShowOnlyMisplaced;

        base.OnUpdate(addon);
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        // don't dispose nodes here
        _checkboxes.Clear();
        _okButton = null;
        base.OnFinalize(addon);
    }
}
