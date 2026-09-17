using GlamourLog.Services;
using GlamourLog.Windows.JobPicker;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace GlamourLog.Nodes;

internal sealed class JobPickerButtonNode : ButtonBase {
    private const float ButtonSize = 36f;
    private const float ForegroundSize = 28f;
    private const string TexturePath = "ui/uld/LFG_hr1.tex";
    private const int ClassJobIconOffset = 62000;

    private readonly PcSearchSelectClassPicker _picker;
    private readonly SimpleImageNode _background;
    private readonly SimpleImageNode _foreground;

    internal JobPickerButtonNode(System.Action? onClick = null) {
        Size = new Vector2(ButtonSize, ButtonSize);
        TextTooltip = Addon.GetRow(294).Text.ToString();
        OnClick = onClick ?? WindowsService.Get().OpenPcSearchSelectClassTest;

        _background = new SimpleImageNode {
            Size = new Vector2(ButtonSize),
            TexturePath = TexturePath,
            WrapMode = WrapMode.Stretch,
        };
        _background.AttachNode(this);

        _foreground = new SimpleImageNode {
            Position = new Vector2((ButtonSize - ForegroundSize) * 0.5f),
            Size = new Vector2(ForegroundSize),
            TexturePath = TexturePath,
            WrapMode = WrapMode.Stretch,
        };
        _foreground.AttachNode(this);

        LoadThreePartTimelines(
            this,
            _background,
            _foreground,
            new Vector2((ButtonSize - ForegroundSize) * 0.5f));
        InitializeComponentEvents();

        _picker = WindowsService.Get().PcSearchSelectClassPicker;
        _picker.SelectionChanged += SyncIcon;
        SyncIcon();
    }

    protected override void Dispose(bool isNativeDestructor) {
        if (!IsDisposed)
            _picker.SelectionChanged -= SyncIcon;
        base.Dispose(isNativeDestructor);
    }

    private void SyncIcon() {
        var selection = _picker.GetSelectionSnapshot();
        var roles = SelectedRoles(selection);
        var (backgroundPartId, foregroundPartId) = PartsFor(roles);

        TextTooltip = SelectionTooltip(selection);

        ApplyPart(_background, backgroundPartId);
        _foreground.IsVisible = foregroundPartId is not null;
        if (foregroundPartId is not null)
            ApplyPart(_foreground, foregroundPartId.Value);
    }

    private static string SelectionTooltip(IReadOnlyList<bool> selection) {
        if (selection.All(selected => selected))
            return Addon.GetRow(970).Text.ToString();

        var classJobs = IDataManager.Get().GetExcelSheet<ClassJob>();
        return string.Join(
            ", ",
            selection
                .Select((selected, index) => (selected, index))
                .Where(entry => entry.selected)
                .Select(entry => {
                    var capturedId = (int)PcSearchSelectClassSetupData.Values[entry.index + 52].Value!;
                    var classJobId = checked((uint)(capturedId - ClassJobIconOffset));
                    return classJobs.GetRow(classJobId).Abbreviation.ToString();
                }));
    }

    private static JobRoleGroup SelectedRoles(IReadOnlyList<bool> selection) {
        var roles = JobRoleGroup.None;
        if (AnySelected(selection, 0, 6))
            roles |= JobRoleGroup.Tank;
        if (AnySelected(selection, 6, 5))
            roles |= JobRoleGroup.Healer;
        if (AnySelected(selection, 11, 21))
            roles |= JobRoleGroup.Dps;
        if (AnySelected(selection, 32, 8))
            roles |= JobRoleGroup.Crafter;
        if (AnySelected(selection, 40, 3))
            roles |= JobRoleGroup.Gatherer;
        return roles;
    }

    private static bool AnySelected(IReadOnlyList<bool> selection, int start, int count) {
        for (var index = start; index < start + count; index++) {
            if (selection[index])
                return true;
        }

        return false;
    }

    private static (int Background, int? Foreground) PartsFor(JobRoleGroup roles)
        => roles switch {
            JobRoleGroup.Tank => (6, 14),
            JobRoleGroup.Healer => (7, 15),
            JobRoleGroup.Dps => (8, 16),
            JobRoleGroup.Crafter => (23, 26),
            JobRoleGroup.Gatherer => (24, 27),
            JobRoleGroup.Crafter | JobRoleGroup.Gatherer => (25, null),
            JobRoleGroup.Tank | JobRoleGroup.Healer => (19, null),
            JobRoleGroup.Tank | JobRoleGroup.Dps => (20, null),
            JobRoleGroup.Healer | JobRoleGroup.Dps => (21, null),
            JobRoleGroup.Tank | JobRoleGroup.Healer | JobRoleGroup.Dps => (22, null),
            _ => (9, 17),
        };

    private static void ApplyPart(SimpleImageNode image, int partId) {
        var part = PartInfo(partId);
        image.TextureCoordinates = part.TextureCoordinates;
        image.TextureSize = part.Size;
    }

    private static UldPartInfo PartInfo(int partId)
        => partId switch {
            6 => new(new Vector2(0f, 8f), new Vector2(36f)),
            7 => new(new Vector2(36f, 8f), new Vector2(36f)),
            8 => new(new Vector2(0f, 44f), new Vector2(36f)),
            9 => new(new Vector2(36f, 44f), new Vector2(36f)),
            14 => new(new Vector2(0f, 108f), new Vector2(28f)),
            15 => new(new Vector2(28f, 108f), new Vector2(28f)),
            16 => new(new Vector2(56f, 108f), new Vector2(28f)),
            17 => new(new Vector2(84f, 108f), new Vector2(28f)),
            19 => new(new Vector2(56f, 136f), new Vector2(36f)),
            20 => new(new Vector2(0f, 172f), new Vector2(36f)),
            21 => new(new Vector2(36f, 172f), new Vector2(36f)),
            22 => new(new Vector2(72f, 172f), new Vector2(36f)),
            23 => new(new Vector2(0f, 252f), new Vector2(36f)),
            24 => new(new Vector2(36f, 252f), new Vector2(36f)),
            25 => new(new Vector2(72f, 252f), new Vector2(36f)),
            26 => new(new Vector2(0f, 288f), new Vector2(28f)),
            27 => new(new Vector2(28f, 288f), new Vector2(28f)),
            _ => throw new ArgumentOutOfRangeException(nameof(partId), partId, null),
        };

    [Flags]
    private enum JobRoleGroup {
        None = 0,
        Tank = 1 << 0,
        Healer = 1 << 1,
        Dps = 1 << 2,
        Crafter = 1 << 3,
        Gatherer = 1 << 4,
    }

    private sealed record UldPartInfo(Vector2 TextureCoordinates, Vector2 Size);
}
