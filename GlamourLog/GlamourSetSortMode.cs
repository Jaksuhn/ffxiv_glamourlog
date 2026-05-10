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
    internal static ListSortDirection DefaultDirection(this GlamourSetSortMode mode) => mode switch {
        GlamourSetSortMode.Alphabetical => ListSortDirection.Ascending,
        GlamourSetSortMode.ItemLevel => ListSortDirection.Descending,
        GlamourSetSortMode.Patch => ListSortDirection.Descending,
        _ => ListSortDirection.Ascending,
    };
}
