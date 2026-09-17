using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using KamiToolKit.Classes;
using System.Globalization;

namespace GlamourLog.Windows.JobPicker;

internal unsafe sealed class PcSearchSelectClassPicker : IDisposable {
    internal const string AddonName = "PcSearchSelectClass";
    private const uint OpenAddonValueCount = 0x88;
    private const int ClassJobIconOffset = 62000;

    private CustomEventInterface? _eventInterface;
    private readonly bool[] _selectedStates = new bool[43];
    private ushort _openedAddonId;
    private bool _disposeRequested;
    private bool _lifecycleRegistered = true;

    internal PcSearchSelectClassPicker() {
        ResetSelectionFromSetup();
        IAddonLifecycle.Get().RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    internal event System.Action? SelectionChanged;

    internal bool[] GetSelectionSnapshot() => [.. _selectedStates];

    internal bool HasOpenedAddon {
        get {
            var addon = IGameGui.Get().GetAddonByName<AtkUnitBase>(AddonName);
            return addon is not null && addon->Id == _openedAddonId;
        }
    }

    internal IReadOnlyList<uint> GetSelectedClassJobIds()
        => _selectedStates
            .Select((selected, index) => (selected, index))
            .Where(entry => entry.selected)
            .Select(entry => ClassJobIdAt(entry.index))
            .ToArray();

    internal void SetSelectedClassJobIds(IReadOnlyCollection<uint> classJobIds) {
        var selectAll = classJobIds.Count == 0;
        for (var index = 0; index < _selectedStates.Length; index++)
            _selectedStates[index] = selectAll || classJobIds.Contains(ClassJobIdAt(index));
        SelectionChanged?.Invoke();
    }

    internal void Open(ushort parentAddonId) {
        if (_disposeRequested)
            return;

        var existing = IGameGui.Get().GetAddonByName<AtkUnitBase>(AddonName);
        if (existing is not null) {
            IPluginLog.Get().Warning(
                $"[{nameof(PcSearchSelectClassPicker)}] {AddonName} is already open. Close it before opening the test picker.");
            return;
        }

        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule is null) {
            IPluginLog.Get().Error($"[{nameof(PcSearchSelectClassPicker)}] RaptureAtkModule is unavailable.");
            return;
        }

        var addonNameId = ResolveAddonNameId(raptureAtkModule);
        if (addonNameId is null) {
            IPluginLog.Get().Error(
                $"[{nameof(PcSearchSelectClassPicker)}] Could not resolve {AddonName} in RaptureAtkModule.AddonNames.");
            return;
        }

        using var values = new RentedAtkValues(PcSearchSelectClassSetupData.CapturedValueCount);
        _eventInterface = new CustomEventInterface(ReceiveEvent);

        try {
            PopulateAtkValues(values.Span);
            _openedAddonId = raptureAtkModule->OpenAddon(
                addonNameId.Value,
                OpenAddonValueCount,
                values,
                _eventInterface,
                eventKind: 2,
                parentAddonId,
                depthLayer: 4);

            if (_openedAddonId == 0) {
                DisposeEventInterface();
                IPluginLog.Get().Error($"[{nameof(PcSearchSelectClassPicker)}] OpenAddon failed for {AddonName}.");
                return;
            }

            var openedAddon = IGameGui.Get().GetAddonByName<AtkUnitBase>(AddonName);
            var liveValueCount = openedAddon is null ? 0 : openedAddon->AtkValuesCount;
            IPluginLog.Get().Info(
                $"[{nameof(PcSearchSelectClassPicker)}] Opened {AddonName}: " +
                $"addon ID={_openedAddonId}, name-table index={addonNameId.Value}, " +
                $"OpenAddon value count={OpenAddonValueCount}, setup buffer count={values.Span.Length}, " +
                $"live AtkValue count={liveValueCount}, parent addon ID={parentAddonId}.");
        }
        catch (Exception ex) {
            _openedAddonId = 0;
            DisposeEventInterface();
            IPluginLog.Get().Error(ex, $"[{nameof(PcSearchSelectClassPicker)}] Failed to open {AddonName}.");
        }
    }

    public void Dispose() {
        if (_disposeRequested)
            return;

        _disposeRequested = true;
        var addon = IGameGui.Get().GetAddonByName<AtkUnitBase>(AddonName);
        if (addon is not null && addon->Id == _openedAddonId) {
            addon->Close(false);
            return;
        }

        _openedAddonId = 0;
        DisposeEventInterface();
        UnregisterLifecycle();
    }

    private AtkValue* ReceiveEvent(
        AtkModuleInterface.AtkEventInterface* eventInterface,
        AtkValue* returnValue,
        AtkValue* values,
        uint valueCount,
        ulong eventKind) {
        if (values is null) {
            IPluginLog.Get().Warning(
                $"[{nameof(PcSearchSelectClassPicker)}] Received callback with no values; " +
                $"event kind={eventKind}, count={valueCount}.");
            returnValue->SetBool(true);
            return returnValue;
        }

        if (valueCount == 1 && values[0].Type == AtkValueType.Int && values[0].Int == -1) {
            var dismissedSelection = SelectedIndices();
            IPluginLog.Get().Info(
                $"[{nameof(PcSearchSelectClassPicker)}] Picker dismissed: " +
                $"selected count={dismissedSelection.Count}, selected indices=[{string.Join(", ", dismissedSelection)}].");
            returnValue->SetBool(true);
            return returnValue;
        }

        if (valueCount != _selectedStates.Length + 1) {
            IPluginLog.Get().Warning(
                $"[{nameof(PcSearchSelectClassPicker)}] Unexpected selection callback count: {valueCount}.");
            returnValue->SetBool(true);
            return returnValue;
        }

        for (var i = 0; i < _selectedStates.Length; i++) {
            if (values[i].Type != AtkValueType.UInt) {
                IPluginLog.Get().Warning(
                    $"[{nameof(PcSearchSelectClassPicker)}] Unexpected selection type at {i}: {values[i].Type}.");
                returnValue->SetBool(true);
                return returnValue;
            }

            _selectedStates[i] = values[i].UInt != 0;
        }

        SelectionChanged?.Invoke();
        var selectedIndices = SelectedIndices();
        IPluginLog.Get().Info(
            $"[{nameof(PcSearchSelectClassPicker)}] Selection callback: event kind={eventKind}, " +
            $"value count={valueCount}, selected count={selectedIndices.Count}, " +
            $"selected indices=[{string.Join(", ", selectedIndices)}].");
        returnValue->SetBool(false);
        return returnValue;
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args) {
        if (args.Addon.IsNull || args.Addon.Id != _openedAddonId)
            return;

        _openedAddonId = 0;
        DisposeEventInterface();

        if (_disposeRequested)
            UnregisterLifecycle();
    }

    private static uint? ResolveAddonNameId(RaptureAtkModule* raptureAtkModule) {
        for (long index = 0; index < raptureAtkModule->AddonNames.LongCount; index++) {
            if (raptureAtkModule->AddonNames[index].ToString() == AddonName)
                return checked((uint)index);
        }

        return null;
    }

    private void PopulateAtkValues(Span<AtkValue> destination) {
        if (destination.Length != PcSearchSelectClassSetupData.Values.Length)
            throw new InvalidOperationException(
                $"{AddonName} setup count mismatch: expected {destination.Length}, " +
                $"got {PcSearchSelectClassSetupData.Values.Length}.");

        ValidateSetupRange(0, 9, AtkValueType.ConstString);
        ValidateSetupRange(9, 43, AtkValueType.UInt);
        ValidateSetupRange(52, 43, AtkValueType.Int);
        ValidateSetupRange(95, 44, AtkValueType.String);

        for (var i = 0; i < destination.Length; i++) {
            var snapshot = PcSearchSelectClassSetupData.Values[i];
            ref var value = ref destination[i];
            value.Type = snapshot.Type;

            switch (snapshot.Type) {
                case AtkValueType.Undefined:
                case AtkValueType.Null:
                    break;
                case AtkValueType.Bool:
                    value.Bool = (bool)snapshot.Value!;
                    break;
                case AtkValueType.Int:
                    value.Int = (int)snapshot.Value!;
                    break;
                case AtkValueType.Int64:
                    value.Int64 = (long)snapshot.Value!;
                    break;
                case AtkValueType.UInt:
                    value.UInt = i is >= 9 and < 52
                        ? (_selectedStates[i - 9] ? 1U : 0U)
                        : (uint)snapshot.Value!;
                    break;
                case AtkValueType.UInt64:
                    value.UInt64 = (ulong)snapshot.Value!;
                    break;
                case AtkValueType.Float:
                    value.Float = (float)snapshot.Value!;
                    break;
                case AtkValueType.String:
                case AtkValueType.ManagedString:
                case AtkValueType.ConstString:
                    value.SetManagedString((string)snapshot.Value!);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Cannot replay {AddonName} AtkValue {i} with type {FormatType(snapshot.Type)}.");
            }
        }
    }

    private static void ValidateSetupRange(int start, int count, AtkValueType expectedType) {
        for (var index = start; index < start + count; index++) {
            var actualType = PcSearchSelectClassSetupData.Values[index].Type;
            if (actualType != expectedType)
                throw new InvalidOperationException(
                    $"{AddonName} setup value {index} should be {expectedType}, but is {actualType}.");
        }
    }

    private void ResetSelectionFromSetup() {
        for (var i = 0; i < _selectedStates.Length; i++)
            _selectedStates[i] = (uint)PcSearchSelectClassSetupData.Values[i + 9].Value! != 0;
    }

    private static uint ClassJobIdAt(int selectionIndex) {
        var capturedId = (int)PcSearchSelectClassSetupData.Values[selectionIndex + 52].Value!;
        return checked((uint)(capturedId - ClassJobIconOffset));
    }

    private List<int> SelectedIndices() {
        var selectedIndices = new List<int>();
        for (var i = 0; i < _selectedStates.Length; i++) {
            if (_selectedStates[i])
                selectedIndices.Add(i);
        }

        return selectedIndices;
    }

    private void DisposeEventInterface() {
        _eventInterface?.Dispose();
        _eventInterface = null;
    }

    private void UnregisterLifecycle() {
        if (!_lifecycleRegistered)
            return;
        IAddonLifecycle.Get().UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
        _lifecycleRegistered = false;
    }

    private static string FormatType(AtkValueType type)
        => ((int)type).ToString(CultureInfo.InvariantCulture);
}
