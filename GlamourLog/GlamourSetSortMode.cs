using System.ComponentModel;

namespace GlamourLog;

public enum GlamourSetSortMode {
    [Description("Alphabetical")]
    AlphabeticalAscending,

    [Description("Item level")]
    ItemLevelDescending,

    [Description("Patch")]
    PatchDescending,
}
