namespace GlamourLog.Features.Cabinet;

internal readonly struct CabinetItemRenderer {
    internal uint ItemId { get; init; }
    internal bool IsStorable { get; init; }
    internal string? ItemName { get; init; }
}
