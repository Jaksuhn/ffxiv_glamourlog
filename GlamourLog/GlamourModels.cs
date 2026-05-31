namespace GlamourLog;

internal readonly record struct SourceNavigateTarget(uint TerritoryTypeId, Vector3 WorldPosition);

internal sealed class GlamourSet {
    public required uint ItemId { get; init; }
    public required string Name { get; init; }
    /// <summary> <see cref="OutfitCategory.Name"/> when matched; null if bucket is unobtainable or legacy PvP with no costs.</summary>
    public required string? CategoryName { get; init; }
    public required bool IsUnobtainable { get; init; }
    public required IReadOnlyList<uint> Items { get; init; }
    public required uint SortItemLevel { get; init; }
    /// <summary> From <see cref="AllaganLib.GameSheets.Sheets.ItemSheet.GetItemPatch"/> over set pieces </summary>
    public required decimal SortPatchNo { get; init; }
    public bool NonSetCabinetPiece { get; init; }
    public required bool IsIncompatible { get; init; } // race/sex restricted items
    public required SetModelSignature ModelSignature { get; init; }
    public required int SharedModelGroupSize { get; init; }
}

internal enum ItemStorageState {
    None,
    DresserSet,
    DresserLoose,
    Armoire,
}

internal enum SetStorageState {
    None,
    Dresser,
    Armoire,
    Mixed,
}
