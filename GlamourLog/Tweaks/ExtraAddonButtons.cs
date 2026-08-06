using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Tweaks.Cabinet;
using GlamourLog.Tweaks.PrismBox;
using GlamourLog.Services;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using System.Threading.Tasks;

namespace GlamourLog.Tweaks;

internal sealed class ExtraAddonButtons : IPluginService, IAsyncDisposable {
    public int InitOrder => 15;

    private const uint CabinetPrevArrowId = 3;
    private const uint CabinetNextArrowId = 4;
    private const uint CrystallizePrevArrowId = 6;
    private const uint CrystallizeNextArrowId = 7;

    private readonly AddonController _cabinetController;
    private readonly AddonController _crystallizeController;

    private CircleButtonNode? _cabinetFilterButton;
    private CircleButtonNode? _cabinetStoreButton;
    private CircleButtonNode? _crystallizeFilterButton;
    private CircleButtonNode? _crystallizeStoreButton;

    public unsafe ExtraAddonButtons() {
        _cabinetController = new AddonController {
            AddonName = "Cabinet",
            OnSetup = OnCabinetSetup,
            OnFinalize = OnCabinetFinalize,
            OnUpdate = OnCabinetUpdate,
        };
        _crystallizeController = new AddonController {
            AddonName = CrystallizeNativeTree.AddonName,
            OnSetup = OnCrystallizeSetup,
            OnFinalize = OnCrystallizeFinalize,
            OnUpdate = OnCrystallizeUpdate,
        };

        IFramework.Get().Run(() => {
            _cabinetController.Enable();
            _crystallizeController.Enable();
        });
    }

    private unsafe void OnCabinetSetup(AtkUnitBase* addon) {
        DisposeCabinetButtons();

        if (!TryGetArrowPair(addon, CabinetPrevArrowId, CabinetNextArrowId, out var prev, out var next))
            return;

        var (filterPos, storePos, size) = ComputeAfterArrowPositions(addon, prev, next);

        _cabinetFilterButton = CreateFilterButton(filterPos, size, () => {
            var windows = WindowsService.Get();
            var origin = FilterOriginNearButton(_cabinetFilterButton, AddonFilterWindow.WindowWidth);
            windows.AddonFilterWindow.OpenOrToggleNear(AddonFilterKind.Armoire, Addon.GetRow(7542).Text.ToString(), AddonFilterWindow.ArmoireOptions, origin);
        }, Addon.GetRow(7542).Text.ToString());

        _cabinetStoreButton = CreateStoreButton(storePos, size, () => {
            if (AtkUnitBase.IsAddonReady("Cabinet"))
                Svc.Automation.Start(new StoreAllArmoireTask());
        });

        _cabinetFilterButton.AttachNode(addon);
        _cabinetStoreButton.AttachNode(addon);
    }

    private unsafe void OnCrystallizeSetup(AtkUnitBase* addon) {
        DisposeCrystallizeButtons();

        if (!TryGetArrowPair(addon, CrystallizePrevArrowId, CrystallizeNextArrowId, out var prev, out var next))
            return;

        var (filterPos, storePos, size) = ComputeAboveArrowPositions(addon, prev, next);

        _crystallizeFilterButton = CreateFilterButton(filterPos, size, () => {
            var windows = WindowsService.Get();
            var origin = FilterOriginNearButton(_crystallizeFilterButton, AddonFilterWindow.WindowWidth);
            windows.AddonFilterWindow.OpenOrToggleNear(AddonFilterKind.Dresser, Addon.GetRow(7542).Text.ToString(), AddonFilterWindow.DresserOptions, origin);
        }, Addon.GetRow(7542).Text.ToString());

        _crystallizeStoreButton = CreateStoreButton(storePos, size, () => {
            if (AtkUnitBase.IsAddonReady(CrystallizeNativeTree.AddonName))
                Svc.Automation.Start(new StoreAllDresserTask());
        });

        _crystallizeFilterButton.AttachNode(addon);
        _crystallizeStoreButton.AttachNode(addon);
    }

    private unsafe void OnCabinetFinalize(AtkUnitBase* _) {
        DisposeCabinetButtons();
        var filter = WindowsService.Get().AddonFilterWindow;
        if (filter.ActiveKind == AddonFilterKind.Armoire)
            filter.CloseIfOpen();
    }

    private unsafe void OnCrystallizeFinalize(AtkUnitBase* _) {
        DisposeCrystallizeButtons();
        var filter = WindowsService.Get().AddonFilterWindow;
        if (filter.ActiveKind == AddonFilterKind.Dresser)
            filter.CloseIfOpen();
    }

    private unsafe void OnCabinetUpdate(AtkUnitBase* _) {
        if (_cabinetFilterButton is null)
            return;
        var filter = WindowsService.Get().AddonFilterWindow;
        _cabinetFilterButton.Icon = filter is { IsOpen: true, ActiveKind: AddonFilterKind.Armoire } ? CircleButtonIcon.ActiveGearCog : CircleButtonIcon.GearCog;
    }

    private unsafe void OnCrystallizeUpdate(AtkUnitBase* _) {
        if (_crystallizeFilterButton is null)
            return;
        var filter = WindowsService.Get().AddonFilterWindow;
        _crystallizeFilterButton.Icon = filter is { IsOpen: true, ActiveKind: AddonFilterKind.Dresser } ? CircleButtonIcon.ActiveGearCog : CircleButtonIcon.GearCog;
    }

    // for cabinet
    private static unsafe (Vector2 Filter, Vector2 Store, float Size) ComputeAfterArrowPositions(AtkUnitBase* addon, AtkResNode* prev, AtkResNode* next) {
        var prevPos = ToRootLocal(addon, prev);
        var nextPos = ToRootLocal(addon, next);
        var size = ArrowButtonSize(prev);
        var step = nextPos.X - prevPos.X;
        var y = nextPos.Y + (next->Height - size) * 0.5f;
        var filter = new Vector2(nextPos.X + step, y);
        var store = new Vector2(filter.X + step, y);
        return (filter, store, size);
    }

    // for dresser
    private static unsafe (Vector2 Filter, Vector2 Store, float Size) ComputeAboveArrowPositions(AtkUnitBase* addon, AtkResNode* prev, AtkResNode* next) {
        var prevPos = ToRootLocal(addon, prev);
        var nextPos = ToRootLocal(addon, next);
        var size = ArrowButtonSize(prev);
        var gap = Math.Max(0f, nextPos.X - prevPos.X - prev->Width);
        var y = prevPos.Y - gap - size;
        var filter = new Vector2(prevPos.X + (prev->Width - size) * 0.5f, y);
        var store = new Vector2(nextPos.X + (next->Width - size) * 0.5f, y);
        return (filter, store, size);
    }

    private static unsafe bool TryGetArrowPair(AtkUnitBase* addon, uint prevId, uint nextId, out AtkResNode* prev, out AtkResNode* next) {
        prev = addon->GetNodeById(prevId);
        next = addon->GetNodeById(nextId);
        return prev is not null && next is not null;
    }

    private static unsafe float ArrowButtonSize(AtkResNode* arrow)
        => Math.Min(arrow->Width, arrow->Height);

    private static unsafe Vector2 ToRootLocal(AtkUnitBase* addon, AtkResNode* node) {
        var scale = addon->Scale;
        if (scale <= 0f)
            scale = 1f;
        // screen coords are scaled, pos is unscaled
        return new Vector2((node->ScreenX - addon->X) / scale, (node->ScreenY - addon->Y) / scale);
    }

    private static CircleButtonNode CreateFilterButton(Vector2 position, float size, System.Action onClick, string tooltip)
        => new() {
            Icon = CircleButtonIcon.GearCog,
            Size = new Vector2(size),
            Position = position,
            OnClick = onClick,
            TextTooltip = tooltip,
        };

    private static CircleButtonNode CreateStoreButton(Vector2 position, float size, System.Action onClick)
        => new() {
            Icon = CircleButtonIcon.Chest,
            Size = new Vector2(size),
            Position = position,
            OnClick = onClick,
            TextTooltip = "Store all eligible items",
        };

    private static Vector2 FilterOriginNearButton(CircleButtonNode? button, float windowWidth) {
        if (button is null)
            return new Vector2(80f, 80f);

        var screen = button.ScreenPosition;
        return new Vector2(
            screen.X - windowWidth * 0.5f + button.Size.X * 0.5f,
            screen.Y + button.Size.Y + 8f);
    }

    private void DisposeCabinetButtons() {
        _cabinetFilterButton?.Dispose();
        _cabinetStoreButton?.Dispose();
        _cabinetFilterButton = null;
        _cabinetStoreButton = null;
    }

    private void DisposeCrystallizeButtons() {
        _crystallizeFilterButton?.Dispose();
        _crystallizeStoreButton?.Dispose();
        _crystallizeFilterButton = null;
        _crystallizeStoreButton = null;
    }

    public async ValueTask DisposeAsync() {
        await Svc.Framework.RunOnFrameworkThread(() => {
            _cabinetController.Dispose();
            _crystallizeController.Dispose();
            DisposeCabinetButtons();
            DisposeCrystallizeButtons();
        });
    }
}
