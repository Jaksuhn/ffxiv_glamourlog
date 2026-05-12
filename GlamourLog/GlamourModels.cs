namespace GlamourLog;

/// <summary> World navigation target for vendor / quest headers (territory type row id + world XZ from Lumina / Allagan NPC locations).</summary>
internal readonly record struct SourceNavigateTarget(uint TerritoryTypeId, Vector3 WorldPosition);

internal sealed class GlamourSet {
    public required uint ItemId { get; init; }
    public required string Name { get; init; }
    /// <summary> <see cref="OutfitCategory.Name"/> when matched; null if bucket is unobtainable or legacy PvP with no costs.</summary>
    public required string? CategoryName { get; init; }
    public required bool IsUnobtainable { get; init; }
    public required IReadOnlyList<uint> Items { get; init; }
    /// <summary> From <see cref="Item.LevelItem"/> on the set token row; used for list sorting. </summary>
    public required uint SortItemLevel { get; init; }
    /// <summary> From <see cref="AllaganLib.GameSheets.Sheets.ItemSheet.GetItemPatch"/> over set pieces (mirage token <see cref="ItemId"/> is not an item row); used for list sorting. </summary>
    public required decimal SortPatchNo { get; init; }
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
