using System.ComponentModel;
using clib.Configuration;
using Dalamud.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GlamourLog;

[Serializable]
public class Configuration : IPluginConfiguration, IPluginService {
    [JsonIgnore] public static Configuration C => Configuration.Get();

    public int Version { get; set; } = 1;

    public FilterType FilterCompleted { get; set; }
    public FilterType FilterIncompatible { get; set; }
    public FilterType FilterUnobtainable { get; set; }
    public FilterType FilterMogstation { get; set; }
    public FilterType FilterStarted { get; set; }
    public FilterType FilterAffordable { get; set; }
    public FilterType FilterContributable { get; set; }
    public FilterType FilterTradeable { get; set; }
    public FilterType FilterArmoire { get; set; }
    public FilterType FilterMisplaced { get; set; }
    public FilterType FilterSharedModels { get; set; }

    public GlamourSetSortMode SetListSortMode { get; set; } = GlamourSetSortMode.Alphabetical;
    public ListSortDirection SetListSortDirection { get; set; } = ListSortDirection.Ascending;

    public bool DisableClose { get; set; } = true;
    public bool PersistSearch { get; set; }

    public bool HideCabinetOwnedItems { get; set; }
    public bool HideCabinetGearsetItems { get; set; }

    public bool HideCrystallizeOwnedItems { get; set; }
    public bool HideCrystallizeArmoireEligibleItems { get; set; }
    public bool HideCrystallizeNonOutfitItems { get; set; }

    public void Save() => Svc.Interface.SavePluginConfig(this);

    [JsonExtensionData]
    private IDictionary<string, JToken>? LegacySettings { get; set; }

    private sealed class Version1Migration : IConfigMigration<Configuration> {
        public int TargetVersion => 1;

        public void Migrate(Configuration config) {
            config.FilterCompleted = Read(config, "ShowOnlyCompleted") ? FilterType.Only : Read(config, "HideCompleted") ? FilterType.Exclude : FilterType.Include;
            config.FilterIncompatible = Read(config, "HideIncompatible") ? FilterType.Exclude : FilterType.Include;
            config.FilterUnobtainable = Read(config, "HideUnobtainable") ? FilterType.Exclude : FilterType.Include;
            config.FilterMogstation = Read(config, "HideMogstation") ? FilterType.Exclude : FilterType.Include;
            config.FilterStarted = Read(config, "HideNonPartials") ? FilterType.Only : FilterType.Include;
            config.FilterAffordable = Read(config, "HideUnaffordable") ? FilterType.Only : FilterType.Include;
            config.FilterContributable = Read(config, "HideUnready") ? FilterType.Only : FilterType.Include;
            config.FilterTradeable = Read(config, "HideNoMarketboard") ? FilterType.Only : FilterType.Include;
            config.FilterMisplaced = Read(config, "ShowOnlyMisplaced") ? FilterType.Only : FilterType.Include;
            config.FilterSharedModels = Read(config, "HideSharedModels") ? FilterType.Exclude : FilterType.Include;
            config.LegacySettings?.Clear();
        }

        private static bool Read(Configuration config, string key)
            => config.LegacySettings?.TryGetValue(key, out var value) == true && value.Value<bool>();
    }
}
