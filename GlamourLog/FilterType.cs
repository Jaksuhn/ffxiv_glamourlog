namespace GlamourLog;

public enum FilterType {
    Include,
    Only,
    Exclude,
}

internal static class FilterTypeExtensions {
    internal static string Label(this FilterType type) => type switch {
        FilterType.Include => UIStrings.FilterType_Include,
        FilterType.Only => UIStrings.FilterType_Only,
        FilterType.Exclude => UIStrings.FilterType_Exclude,
        _ => type.ToString(),
    };
}
