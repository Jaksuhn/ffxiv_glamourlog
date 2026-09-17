using AllaganLib.GameSheets.Caches;

namespace GlamourLog;

internal readonly record struct SourceFilterOption(ItemInfoType? Type, string Label) {
    internal static SourceFilterOption All { get; } = new(null, "All sources");
}

internal readonly record struct SourceSubFilterOption(string Key, string Label);
