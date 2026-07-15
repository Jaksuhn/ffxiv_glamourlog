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
    /// <summary> Some but not all pieces share a model with items in other sets (full-set match uses <see cref="SharedModelGroupSize"/> instead). </summary>
    public required bool HasPartialSharedModels { get; init; }
}

/// <summary>Where a piece is for a specific set (OutfitSlot is set-scoped).</summary>
internal enum PieceLocation {
    None,
    Inventory,
    LooseDresser,
    OutfitSlot,
    Armoire,
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

/// <summary>Transient per-piece ownership for one ownership query.</summary>
internal sealed class PieceStatus {
    internal required uint ItemId { get; init; }
    internal required PieceLocation Location { get; init; }
    internal required ItemStorageState DisplayStorage { get; init; }
    internal required bool ShowArmoireWarning { get; init; }

    internal bool IsOwned => Location is not PieceLocation.None;
    internal bool IsStored => Location is PieceLocation.Armoire or PieceLocation.LooseDresser or PieceLocation.OutfitSlot;
}

/// <summary>Transient ownership/status for one catalog set for one ownership query.</summary>
internal sealed class SetStatus {
    internal required GlamourSet Set { get; init; }
    internal required IReadOnlyList<PieceStatus> Pieces { get; init; }
    internal required bool IsComplete { get; init; }
    internal required int OwnedCount { get; init; }
    internal required SetStorageState Storage { get; init; }
    internal required bool ArmoireMisplaced { get; init; }
    internal required bool HasContributableInventoryPiece { get; init; }
    internal required bool CanAffordMissing { get; init; }

    /// <summary>Started but not checkmark-complete. Cabinet: any owned; mirage: owned count strictly between 0 and total.</summary>
    internal bool IsPartial => !IsComplete && OwnedCount > 0 && (Set.NonSetCabinetPiece || OwnedCount < Pieces.Count);

    internal PieceStatus? Piece(uint itemId)
        => Pieces.FirstOrDefault(p => p.ItemId == itemId);
}
