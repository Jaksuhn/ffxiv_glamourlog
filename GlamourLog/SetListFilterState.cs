using AllaganLib.GameSheets.Caches;

namespace GlamourLog;

internal sealed class SetListFilterState {
    internal List<uint> ClassJobIds { get; set; } = [];
    internal bool PartialClassJobMatch { get; set; } = true;
    internal HashSet<uint> ExpansionRowIds { get; set; } = [];
    internal HashSet<decimal> PatchNumbers { get; set; } = [];
    internal bool UseCustomPatches { get; set; }
    internal ItemInfoType? Source { get; set; }
    internal string SubSource { get; set; } = string.Empty;
    internal uint CurrencyItemId { get; set; }
}
