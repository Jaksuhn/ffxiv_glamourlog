using System.ComponentModel;

namespace GlamourLog;

public enum GlamourSetSortMode {
    [Description("Alphabetical")]
    Alphabetical,

    [Description("Item level")]
    ItemLevel,

    [Description("Patch")]
    Patch,
}

internal static class GlamourSetSortModeExtensions {
    internal static string Label(this GlamourSetSortMode mode) => mode switch {
        GlamourSetSortMode.Alphabetical => UIStrings.Enum_GlamourSetSortMode_Alphabetical,
        GlamourSetSortMode.ItemLevel => UIStrings.Enum_GlamourSetSortMode_ItemLevel,
        GlamourSetSortMode.Patch => UIStrings.Enum_GlamourSetSortMode_Patch,
        _ => mode.ToString(),
    };

    internal static ListSortDirection DefaultDirection(this GlamourSetSortMode mode) => mode switch {
        GlamourSetSortMode.Alphabetical => ListSortDirection.Ascending,
        GlamourSetSortMode.ItemLevel => ListSortDirection.Descending,
        GlamourSetSortMode.Patch => ListSortDirection.Descending,
        _ => ListSortDirection.Ascending,
    };
}
