namespace GlamourLog;

/// <summary> Resolved <see cref="DungeonChestItem"/> → <see cref="DungeonChest"/>: supplemental <see cref="DungeonChestItem.ChestId"/> equals that chest row’s <see cref="DungeonChest.RowId"/> (not <see cref="DungeonChest.ChestId"/>).</summary>
internal readonly record struct DungeonChestItemProvenance(uint ContentFinderConditionId, byte ChestNo, uint TerritoryTypeId);
internal readonly record struct FateItemProvenance(uint FateId); // TODO: better names for these

/// <summary> One duty (CFC) with chest rows for the Sources panel.</summary>
internal sealed record DutyChestSourceGroup(uint ContentFinderConditionId, string DutyName, IReadOnlyList<ChestSourceRow> ChestRows);

/// <summary> One chest bucket under a duty with item/currency icons to show.</summary>
internal sealed record ChestSourceRow(byte ChestNo, uint TerritoryTypeId, IReadOnlyList<uint> ItemIds);

internal sealed class GlamourSet {
    public required uint ItemId { get; init; }
    public required string Name { get; init; }
    /// <summary> <see cref="OutfitCategory.Name"/> when matched; null if bucket is unobtainable or legacy PvP with no costs.</summary>
    public required string? CategoryName { get; init; }
    public required bool IsUnobtainable { get; init; }
    public required IReadOnlyList<uint> Items { get; init; }
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
