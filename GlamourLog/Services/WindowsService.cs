using GlamourLog.Nodes;
using GlamourLog.Windows;
using GlamourLog.Windows.GuideWindow;
using GlamourLog.Windows.JobPicker;
using KamiToolKit.BaseTypes;
using System.Threading.Tasks;

namespace GlamourLog.Services;

internal sealed class WindowsService : IPluginService, IAsyncDisposable {
    private FilterWindow? _filterWindow;
    private AddonFilterWindow? _addonFilterWindow;
    private PatchPickerWindow? _patchPickerWindow;
    private GuideWindow? _mainMenuWindow;
    private LogWindow? _logWindow;
    private PcSearchSelectClassPicker? _pcSearchSelectClassPicker;

    public WindowsService() {
        Svc.Interface.UiBuilder.OpenMainUi += ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi += ToggleMainMenu;
    }

    internal FilterWindow FilterWindow => _filterWindow ??= new FilterWindow {
        InternalName = "GlamourLogFilter",
        Title = string.Empty,
        Subtitle = string.Empty,
        Size = new Vector2(FilterWindow.WindowWidth, FilterWindow.WindowHeight),
        RememberClosePosition = false,
        CreateWindowNode = () => new CompactWindowNode { NodeId = 2 },
    };

    internal AddonFilterWindow AddonFilterWindow => _addonFilterWindow ??= new AddonFilterWindow {
        InternalName = "GlamourLogAddonFilter",
        Title = "Filters",
        Size = new Vector2(AddonFilterWindow.WindowWidth, AddonFilterWindow.HeightFor(2)),
        RememberClosePosition = false,
    };

    internal PatchPickerWindow PatchPickerWindow => _patchPickerWindow ??= new PatchPickerWindow {
        InternalName = "GlamourLogPatchPicker",
        Title = "Custom Patches",
        Size = new Vector2(PatchPickerWindow.WindowWidth, PatchPickerWindow.WindowHeight),
        RememberClosePosition = false,
    };

    internal void ClosePatchPickerIfOpen() => _patchPickerWindow?.CloseIfOpen();

    internal GuideWindow MainMenuWindow => _mainMenuWindow ??= new GuideWindow {
        InternalName = "GlamourLogGuide",
        Title = "Help & Settings",
        Size = new Vector2(GuideWindow.WindowWidth, GuideWindow.WindowHeight),
        RememberClosePosition = false,
    };

    internal LogWindow LogWindow => _logWindow ??= new LogWindow(FilterWindow) {
        InternalName = "GlamourLog",
        Title = "Glamour Log",
        Size = new Vector2(920f, 660f),
        RememberClosePosition = false,
    };

    internal PcSearchSelectClassPicker PcSearchSelectClassPicker
        => _pcSearchSelectClassPicker ??= new PcSearchSelectClassPicker();

    internal void ToggleMainWindow() => IFramework.Get().Run(LogWindow.Toggle);
    internal void ToggleMainMenu() => IFramework.Get().Run(MainMenuWindow.OpenOrToggleCentered);

    internal void ToggleMainMenuNearLogWindow() {
        if (LogWindow.IsOpen)
            MainMenuWindow.OpenOrToggleNear(LogWindow.ComputeMainMenuScreenOrigin());
        else
            MainMenuWindow.OpenOrToggleCentered();
    }

    internal void RefreshLogWindow() => LogWindow.RefreshListsAndDetails();

    internal void OpenPcSearchSelectClassTest() {
        if (!LogWindow.IsOpen || LogWindow.AddonId == 0) {
            IPluginLog.Get().Warning(
                $"[{nameof(WindowsService)}] Open the Glamour Log window before opening the test job picker.");
            return;
        }

        PcSearchSelectClassPicker.Open(checked((ushort)LogWindow.AddonId));
    }

    public async ValueTask DisposeAsync() {
        Svc.Interface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        Svc.Interface.UiBuilder.OpenConfigUi -= ToggleMainMenu;

        // keep alive until every open addon has fully finalized
        if (_pcSearchSelectClassPicker is not null) {
            await IFramework.Get().Run(_pcSearchSelectClassPicker.Dispose);
            while (_pcSearchSelectClassPicker.HasOpenedAddon)
                await Task.Delay(16).ConfigureAwait(false);
        }

        // must be run separately and after from the above
        await Task.Run(async () => {
            await DisposeWindowAsync(_logWindow, nameof(LogWindow)).ConfigureAwait(false);
            await DisposeWindowAsync(_patchPickerWindow, nameof(PatchPickerWindow)).ConfigureAwait(false);
            await DisposeWindowAsync(_filterWindow, nameof(FilterWindow)).ConfigureAwait(false);
            await DisposeWindowAsync(_addonFilterWindow, nameof(AddonFilterWindow)).ConfigureAwait(false);
            await DisposeWindowAsync(_mainMenuWindow, nameof(GuideWindow)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        _filterWindow = null;
        _addonFilterWindow = null;
        _patchPickerWindow = null;
        _mainMenuWindow = null;
        _logWindow = null;
        _pcSearchSelectClassPicker = null;
    }

    private static async ValueTask DisposeWindowAsync(NativeAddon? window, string name) {
        if (window is null)
            return;
        try {
            await window.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) {
            IPluginLog.Get().Error(ex, $"[{nameof(WindowsService)}] Failed to dispose {name}");
        }
    }
}
