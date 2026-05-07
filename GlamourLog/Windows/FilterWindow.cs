using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace GlamourLog;

/// <summary> Filter window for set-list toggles (opened from the main log window cog). </summary>
internal unsafe class FilterWindow(GlamourLogTracker state) : NativeAddon {
    public const float WindowWidth = 456f;
    public const float WindowHeight = 276f;
    private readonly List<CheckboxNode> _checkboxes = [];
    private TextButtonNode? _okButton;
    private bool _hasPendingScreenOrigin;
    private Vector2 _pendingScreenOrigin;

    /// <summary> Opens at <paramref name="screenTopLeft"/> (top-left of this window), or closes if already open. </summary>
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

        var config = Svc.Config;

        var start = ContentStartPosition;
        var rowWidth = ContentSize.X - 16f;
        var x = start.X + 8f;
        var y = start.Y + 8f;
        const float rowHeight = 20f;

        void AddCheckbox(string label, Func<Configuration, bool> read, Action<Configuration> flip) {
            CheckboxNode cb = null!;
            cb = new CheckboxNode {
                Position = new Vector2(x, y),
                Size = new Vector2(rowWidth, rowHeight),
                String = label,
                IsChecked = read(config),
                OnClick = _ => {
                    flip(config);
                    cb.IsChecked = read(config);
                    config.Save();
                    state.MarkLogWindowDirty();
                },
            };
            y += rowHeight + 2f;
            _checkboxes.Add(cb);
            cb.AttachNode(this);
        }

        AddCheckbox("Hide completed sets", c => c.HideCompleted, c => c.HideCompleted ^= true);
        AddCheckbox("Hide unstarted sets", c => c.HideNonPartials, c => c.HideNonPartials ^= true);
        AddCheckbox("Hide sets not ready to be stored", c => c.HideUnready, c => c.HideUnready ^= true);
        AddCheckbox("Hide unaffordable sets", c => c.HideUnaffordable, c => c.HideUnaffordable ^= true);
        AddCheckbox("Hide sets not found on the marketboard", c => c.HideNoMarketboard, c => c.HideNoMarketboard ^= true);

        const float okWidth = 150f;
        const float okHeight = 28f;
        var bottomPad = 10f;
        var checklistBottom = start.Y + 8f + 5f * (rowHeight + 2f);
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
        if (_checkboxes.Count < 5) {
            base.OnUpdate(addon);
            return;
        }

        _checkboxes[0].IsChecked = Svc.Config.HideCompleted;
        _checkboxes[1].IsChecked = Svc.Config.HideNonPartials;
        _checkboxes[2].IsChecked = Svc.Config.HideUnready;
        _checkboxes[3].IsChecked = Svc.Config.HideUnaffordable;
        _checkboxes[4].IsChecked = Svc.Config.HideNoMarketboard;

        base.OnUpdate(addon);
    }

    protected override void OnFinalize(AtkUnitBase* addon) {
        // Same rule as AetherBags InventoryAddonBase / main GlamourLog window: nodes under this
        // NativeAddon are destroyed with the native AtkUnitBase; do not Dispose() them here.
        _checkboxes.Clear();
        _okButton = null;
        base.OnFinalize(addon);
    }
}
