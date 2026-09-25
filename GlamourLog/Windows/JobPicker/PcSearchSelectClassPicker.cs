using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using System.Globalization;
using System.Text;

namespace GlamourLog.Windows.JobPicker;

internal sealed unsafe class PcSearchSelectClassPicker : IDisposable {
    internal const string AddonName = "PcSearchSelectClass";
    private const int ClassJobIconOffset = 62000;

    private CustomEventInterface? _eventInterface;
    private readonly bool[] _selectedStates = new bool[43];
    private AtkUnitBase* _openedAddon;
    private bool _disposeRequested;
    private bool _lifecycleRegistered = true;
    private AtkValue* _setupValues;
    private int _setupValueCount;
    private readonly List<nint> _setupStrings = [];

    internal PcSearchSelectClassPicker() {
        ResetSelectionFromSetup();
        IAddonLifecycle.Get().RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    internal event System.Action? SelectionChanged;

    internal bool[] GetSelectionSnapshot() => [.. _selectedStates];

    internal bool HasOpenedAddon
        => _openedAddon is not null
           && IGameGui.Get().TryGetAddon<AtkUnitBase>(AddonName, out var addon)
           && addon == _openedAddon;

    internal IReadOnlyList<uint> GetSelectedClassJobIds()
        => [.. _selectedStates
            .Select((selected, index) => (selected, index))
            .Where(entry => entry.selected)
            .Select(entry => ClassJobIdAt(entry.index))];

    internal void SetSelectedClassJobIds(IReadOnlyCollection<uint> classJobIds, bool notify = true) {
        var selectAll = classJobIds.Count == 0;
        for (var index = 0; index < _selectedStates.Length; index++)
            _selectedStates[index] = selectAll || classJobIds.Contains(ClassJobIdAt(index));
        if (notify)
            SelectionChanged?.Invoke();
    }

    internal void CloseIfOpen() {
        if (_openedAddon is not null)
            _openedAddon->Close(false);
    }

    internal void Open(ushort parentAddonId) {
        if (_disposeRequested)
            return;

        // using pointers instead of checking addon id cause that's reused
        if (_openedAddon is not null || _setupValues is not null) {
            if (IGameGui.Get().GetAddonByName<AtkUnitBase>(AddonName) is not null) {
                IPluginLog.Get().Warning($"[{nameof(PcSearchSelectClassPicker)}] {AddonName} is already open. Close it before opening again.");
                return;
            }

            ClearOpenedSessionImmediate();
        }

        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule is null) {
            IPluginLog.Get().Error($"[{nameof(PcSearchSelectClassPicker)}] RaptureAtkModule is unavailable.");
            return;
        }

        var addonNameId = ResolveAddonNameId(raptureAtkModule);
        if (addonNameId is null) {
            IPluginLog.Get().Error($"[{nameof(PcSearchSelectClassPicker)}] Could not resolve {AddonName} in RaptureAtkModule.AddonNames.");
            return;
        }

        _eventInterface = new CustomEventInterface(ReceiveEvent);

        try {
            AllocateSetupValues(PcSearchSelectClassSetupData.CapturedValueCount);
            PopulateAtkValues(new Span<AtkValue>(_setupValues, _setupValueCount));
            var openedId = raptureAtkModule->OpenAddon(addonNameId.Value, (uint)_setupValueCount, _setupValues, _eventInterface, eventKind: 2, parentAddonId, depthLayer: 4);
            if (openedId == 0) {
                ClearOpenedSessionImmediate();
                IPluginLog.Get().Error($"[{nameof(PcSearchSelectClassPicker)}] OpenAddon failed for {AddonName}.");
                return;
            }

            var openedAddon = IGameGui.Get().GetAddonByName<AtkUnitBase>(AddonName);
            if (openedAddon is null || openedAddon->Id != openedId) {
                ClearOpenedSessionImmediate();
                IPluginLog.Get().Error($"[{nameof(PcSearchSelectClassPicker)}] Opened {AddonName} but could not resolve its instance.");
                return;
            }

            _openedAddon = openedAddon;
            IPluginLog.Get().Info(
                $"[{nameof(PcSearchSelectClassPicker)}] Opened {AddonName}: " +
                $"addon ID={openedId}, name-table index={addonNameId.Value}, " +
                $"OpenAddon value count={_setupValueCount}, live AtkValue count={openedAddon->AtkValuesCount}, " +
                $"parent addon ID={parentAddonId}.");
        }
        catch (Exception ex) {
            ClearOpenedSessionImmediate();
            IPluginLog.Get().Error(ex, $"[{nameof(PcSearchSelectClassPicker)}] Failed to open {AddonName}.");
        }
    }

    public void Dispose() {
        if (_disposeRequested)
            return;

        _disposeRequested = true;
        if (_openedAddon is not null) {
            _openedAddon->Close(false);
            return;
        }

        ClearOpenedSessionImmediate();
        UnregisterLifecycle();
    }

    private AtkValue* ReceiveEvent(AtkModuleInterface.AtkEventInterface* eventInterface, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind) {
        if (values is null) {
            IPluginLog.Get().Warning($"[{nameof(PcSearchSelectClassPicker)}] Received callback with no values; event kind={eventKind}, count={valueCount}.");
            returnValue->SetBool(true);
            return returnValue;
        }

        if (valueCount == 1 && values[0].Type == AtkValueType.Int && values[0].Int == -1) {
            var dismissedSelection = SelectedIndices();
            IPluginLog.Get().Info($"[{nameof(PcSearchSelectClassPicker)}] Picker dismissed: selected count={dismissedSelection.Count}, selected indices=[{string.Join(", ", dismissedSelection)}].");
            returnValue->SetBool(true);
            return returnValue;
        }

        if (valueCount != _selectedStates.Length + 1) {
            IPluginLog.Get().Warning($"[{nameof(PcSearchSelectClassPicker)}] Unexpected selection callback count: {valueCount}.");
            returnValue->SetBool(true);
            return returnValue;
        }

        for (var i = 0; i < _selectedStates.Length; i++) {
            if (values[i].Type != AtkValueType.UInt) {
                IPluginLog.Get().Warning($"[{nameof(PcSearchSelectClassPicker)}] Unexpected selection type at {i}: {values[i].Type}.");
                returnValue->SetBool(true);
                return returnValue;
            }

            _selectedStates[i] = values[i].UInt != 0;
        }

        // Notify on close only — SyncIcon must not UpdateCollisionNodeList while this child is open.
        var selectedIndices = SelectedIndices();
        IPluginLog.Get().Info($"[{nameof(PcSearchSelectClassPicker)}] Selection callback: event kind={eventKind}, value count={valueCount}, selected count={selectedIndices.Count}, selected indices=[{string.Join(", ", selectedIndices)}].");
        returnValue->SetBool(false);
        return returnValue;
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args) {
        if (args.Addon.IsNull || _openedAddon is null || (AtkUnitBase*)args.Addon.Address != _openedAddon)
            return;

        var eventInterface = _eventInterface;
        var setupValues = _setupValues;
        var setupValueCount = _setupValueCount;
        var setupStrings = _setupStrings.ToArray();
        var notify = SelectionChanged;

        _openedAddon = null;
        _eventInterface = null;
        _setupValues = null;
        _setupValueCount = 0;
        _setupStrings.Clear();

        IFramework.Get().Run(() => {
            try {
                notify?.Invoke();
            }
            catch (Exception ex) {
                IPluginLog.Get().Error(ex, $"[{nameof(PcSearchSelectClassPicker)}] SelectionChanged failed after picker close.");
            }

            FreeSetupSnapshot(setupValues, setupValueCount, setupStrings);
            eventInterface?.Dispose();

            if (_disposeRequested)
                UnregisterLifecycle();
        });
    }

    private void ClearOpenedSessionImmediate() {
        _openedAddon = null;
        var eventInterface = _eventInterface;
        _eventInterface = null;
        eventInterface?.Dispose();
        FreeSetupValues();
    }

    private void AllocateSetupValues(int count) {
        _setupValues = (AtkValue*)MemoryPool.Alloc((ulong)(sizeof(AtkValue) * count));
        _setupValueCount = count;
        new Span<AtkValue>(_setupValues, count).Clear();
    }

    private void FreeSetupValues() {
        var values = _setupValues;
        var count = _setupValueCount;
        var strings = _setupStrings.ToArray();
        _setupValues = null;
        _setupValueCount = 0;
        _setupStrings.Clear();
        FreeSetupSnapshot(values, count, strings);
    }

    private static void FreeSetupSnapshot(AtkValue* values, int count, IReadOnlyList<nint> strings) {
        foreach (var pointer in strings)
            MemoryPool.Free((void*)pointer);

        if (values is null)
            return;

        for (var i = 0; i < count; i++) {
            ref var value = ref values[i];
            if (value.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString) {
                value.Type = AtkValueType.Undefined;
                value.String = default;
            }

            value.Dtor();
        }

        MemoryPool.Free(values);
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
            throw new InvalidOperationException($"{AddonName} setup count mismatch: expected {destination.Length}, got {PcSearchSelectClassSetupData.Values.Length}.");

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
                    AssignSetupString(ref value, (string)snapshot.Value!);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Cannot replay {AddonName} AtkValue {i} with type {FormatType(snapshot.Type)}.");
            }
        }
    }

    private void AssignSetupString(ref AtkValue value, string text) {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var pointer = (byte*)MemoryPool.Alloc((ulong)(byteCount + 1));
        Encoding.UTF8.GetBytes(text, new Span<byte>(pointer, byteCount));
        pointer[byteCount] = 0;
        value.SetString(pointer);
        _setupStrings.Add((nint)pointer);
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

    private void UnregisterLifecycle() {
        if (!_lifecycleRegistered)
            return;
        IAddonLifecycle.Get().UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
        _lifecycleRegistered = false;
    }

    private static string FormatType(AtkValueType type)
        => ((int)type).ToString(CultureInfo.InvariantCulture);
}
