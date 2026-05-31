namespace GlamourLog;

internal readonly record struct ItemModelInfo(ushort Primary, ushort Secondary, ushort Variant, ushort Dye) {
    public static implicit operator ItemModelInfo((ushort Primary, ushort Secondary, ushort Variant, ushort Dye) model)
        => new(model.Primary, model.Secondary, model.Variant, model.Dye);
    public static implicit operator ItemModelInfo(uint itemId)
        => Item.GetRow(itemId).ModelInfo;
}

internal readonly record struct SetModelSignature(bool IsMiscSingle, (uint Slot, ItemModelInfo Model)[] Pieces) {
    internal static SetModelSignature ForMirageSet(IReadOnlyList<uint> pieceIds) {
        var pieces = pieceIds
            .Select(id => {
                var row = Item.GetRow(id);
                return (row.EquipSlot, (ItemModelInfo)row.ModelInfo);
            })
            .OrderBy(p => p.EquipSlot)
            .ToArray();
        return new SetModelSignature(false, pieces);
    }

    internal static SetModelSignature ForMiscSingle(uint itemId) {
        var row = Item.GetRow(itemId);
        return new SetModelSignature(true, [(row.EquipSlot, row.ModelInfo)]);
    }

    public bool Equals(SetModelSignature other) {
        if (IsMiscSingle != other.IsMiscSingle || Pieces.Length != other.Pieces.Length)
            return false;
        for (var i = 0; i < Pieces.Length; i++) {
            if (Pieces[i].Slot != other.Pieces[i].Slot || Pieces[i].Model != other.Pieces[i].Model)
                return false;
        }
        return true;
    }

    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(IsMiscSingle);
        foreach (var piece in Pieces) {
            hash.Add(piece.Slot);
            hash.Add(piece.Model);
        }
        return hash.ToHashCode();
    }
}
