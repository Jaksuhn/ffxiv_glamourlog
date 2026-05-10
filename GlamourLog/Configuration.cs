using Dalamud.Configuration;
using GlamourLog.Services;

namespace GlamourLog;

[Serializable]
public class Configuration : IPluginConfiguration {
    public int Version { get; set; } = 0;

    public bool HideCompleted { get; set; }
    public bool HideNonPartials { get; set; }
    public bool HideUnaffordable { get; set; }
    public bool HideUnready { get; set; }
    public bool HideNoMarketboard { get; set; }
    public bool ShowOnlyMisplaced { get; set; }

    public GlamourSetSortMode SetListSortMode { get; set; } = GlamourSetSortMode.AlphabeticalAscending;

    public void Save() => Svc.Interface.SavePluginConfig(this);
}
