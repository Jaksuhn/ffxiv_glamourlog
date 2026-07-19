namespace GlamourLog;

internal sealed class GlamourSet {
    public required uint ItemId { get; init; }
    public required string Name { get; init; } // display name
    public required string? CategoryName { get; init; } // when null it's either unsorted or unobtainable
    public required bool IsUnobtainable { get; init; }
    public required IReadOnlyList<uint> Items { get; init; }
    public required uint ItemLevel { get; init; } // for sorting
    public required decimal PatchNo { get; init; } // for sorting
    public bool NonSetCabinetPiece { get; init; } // standalone armoire item, not a mirage outfit
    public required bool IsIncompatible { get; init; } // race/sex restricted items
    public required SetModelSignature ModelSignature { get; init; } // visual fingerprint of the whole outfit
    public required int SharedModelGroupSize { get; init; } // how many other sets share this set's full model signature
    public required bool HasPartialSharedModels { get; init; } // for the detail panel when filtering a specific item. Not all sets are full model matches
}

// actual inventory location (per piece)
internal enum PieceLocation {
    None,
    Inventory,
    LooseDresser,
    OutfitSlot,
    Armoire,
}

// mirage location (per piece) for badges
internal enum ItemStorageState {
    None,
    DresserSet,
    DresserLoose,
    Armoire,
}

// mirage location (set cumulative) for badges
internal enum SetStorageState {
    None,
    Dresser,
    Armoire,
    Mixed,
}

// where one piece currently is for a given snapshot
internal sealed class PieceStatus {
    internal required uint ItemId { get; init; }
    internal required PieceLocation Location { get; init; }
    internal required ItemStorageState BadgeLocation { get; init; }
    internal required bool ShowArmoireWarning { get; init; }

    internal bool IsOwned => Location is not PieceLocation.None;
    internal bool IsStored => Location is PieceLocation.Armoire or PieceLocation.LooseDresser or PieceLocation.OutfitSlot;
}

// completion/storage badges for a set in a given snapshot
internal sealed class SetStatus {
    internal required GlamourSet Set { get; init; }
    internal required IReadOnlyList<PieceStatus> Pieces { get; init; }
    internal required bool IsComplete { get; init; }
    internal required int OwnedCount { get; init; }
    internal required SetStorageState Storage { get; init; }
    internal required bool ArmoireMisplaced { get; init; } // armoire-eligible gear sitting in the dresser
    internal required bool HasContributableInventoryPiece { get; init; } // has something in bags that could be stored
    internal required bool CanAffordMissing { get; init; } // can pay the preferred costs for every missing piece

    // owns some pieces but not enough for the checkmark (cabinet: any owned; mirage: more than 0 but not all)
    internal bool IsPartial => !IsComplete && OwnedCount > 0 && (Set.NonSetCabinetPiece || OwnedCount < Pieces.Count);

    internal PieceStatus? Piece(uint itemId) => Pieces.FirstOrDefault(p => p.ItemId == itemId);
}

// nav goal for a given source of an item. Can call navmesh on it via a context menu
internal readonly record struct SourceNavigateTarget(uint TerritoryTypeId, Vector3 WorldPosition);
