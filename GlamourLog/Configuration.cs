using System.ComponentModel;
using Dalamud.Configuration;

namespace GlamourLog;

[Serializable]
public class Configuration : IPluginConfiguration {
    public int Version { get; set; } = 0;

    public bool HideCompleted { get; set; }
    public bool HideIncompatible { get; set; }
    public bool HideNonPartials { get; set; }
    public bool HideUnaffordable { get; set; }
    public bool HideUnready { get; set; }
    public bool HideNoMarketboard { get; set; }
    public bool ShowOnlyMisplaced { get; set; }
    public bool HideSharedModels { get; set; }

    public GlamourSetSortMode SetListSortMode { get; set; } = GlamourSetSortMode.Alphabetical;
    public ListSortDirection SetListSortDirection { get; set; } = ListSortDirection.Ascending;

    public bool DisableClose { get; set; } = true;

    public bool HideCabinetOwnedItems { get; set; }
    public bool HideCabinetGearsetItems { get; set; }

    public void Save() => Svc.Interface.SavePluginConfig(this);
}
