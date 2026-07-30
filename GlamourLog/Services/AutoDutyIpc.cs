using System.Reflection;
using Dalamud.Plugin.Ipc;

namespace GlamourLog.Services;

internal sealed class AutoDutyIpc {
    private static readonly string[] GlamourFarmEnable = [
        "EnablePreLoopActions",
        "AutoRepair",
        "EnableBetweenLoopActions",
        "GlamourChestEntrust",
        "ArmoireEntrust",
        "EnableTerminationActions",
        "StopWhenDutyGathered",
    ];

    private const string SectionStart = "EnablePreLoopActions";
    private const string SectionEnd = "TerminationKeepActive";

    private readonly ICallGateSubscriber<object, bool> _pushOverrides;
    private readonly ICallGateSubscriber<uint, bool> _contentHasPath;
    private readonly ICallGateSubscriber<uint, int, bool, object> _run;

    public AutoDutyIpc() {
        _pushOverrides = Svc.Interface.GetIpcSubscriber<object, bool>("AutoDuty.PushConfigOverrides");
        _contentHasPath = Svc.Interface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
        _run = Svc.Interface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
    }

    internal void FarmOutfit(uint cfcId) {
        if (cfcId == 0 || ContentFinderCondition.GetRowRef(cfcId) is not { IsValid: true, Value.TerritoryType.RowId: > 0 and var territory, Value: var cfc }) {
            Svc.Chat.EchoMessage("GlamourLog: invalid duty.");
            return;
        }

        if (Svc.Get<OwnershipService>().IsContentComplete(cfcId)) {
            Svc.Chat.EchoMessage("All outfit pieces from this duty collected.");
            return;
        }

        if (!_run.HasFunction || !_contentHasPath.HasFunction || !_contentHasPath.InvokeFunc(territory)) {
            Svc.Chat.EchoMessage("AutoDuty has no path for this duty.");
            return;
        }

        var overrides = GetConfigOverrides();
        if (!_pushOverrides.HasFunction || !_pushOverrides.InvokeFunc(overrides)) {
            Svc.Chat.EchoError("Failed to setup AutoDuty for farming.");
            return;
        }

        _run.InvokeAction(territory, 0, true);
    }

    // get all possible configs and set them to default
    private static Dictionary<string, string> GetConfigOverrides() {
        if (TryBuildOverridesFromReflection(out var overrides))
            return overrides;

        return GlamourFarmEnable.ToDictionary(k => k, _ => "true", StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryBuildOverridesFromReflection(out Dictionary<string, string> overrides) {
        overrides = [with(StringComparer.OrdinalIgnoreCase)];

        var configType = GetConfigType();
        if (configType is null)
            return false;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var boolFields = configType.GetFields(flags)
            .Where(f => f.FieldType == typeof(bool))
            .Select(f => (Field: f, Name: CleanConfigName(f.Name)))
            .Where(x => !x.Name.Equals("Version", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Field.MetadataToken)
            .ToList();

        if (boolFields.Count == 0)
            return false;

        var start = boolFields.FindIndex(x => x.Name.Equals(SectionStart, StringComparison.OrdinalIgnoreCase));
        var end = boolFields.FindIndex(x => x.Name.Equals(SectionEnd, StringComparison.OrdinalIgnoreCase));
        if (start < 0 || end < 0 || end < start)
            return false;

        for (var i = start; i <= end; i++)
            overrides[boolFields[i].Name] = "false";

        foreach (var key in GlamourFarmEnable)
            overrides[key] = "true";

        return true;
    }

    private static Type? GetConfigType()
        => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name?.Equals("AutoDuty", StringComparison.OrdinalIgnoreCase) == true)
            ?.GetType("AutoDuty.Windows.Configuration");

    // ConfigHelper.FindConfig
    private static string CleanConfigName(string fieldName)
        => fieldName.Replace(">k__BackingField", "", StringComparison.Ordinal).Replace("<", "", StringComparison.Ordinal);
}
