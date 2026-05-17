using System.Collections.Frozen;

namespace GlamourLog.Features.Cabinet;

internal static class CabinetItemNameLookup {
    private static readonly Lazy<FrozenDictionary<string, uint>> NameToItemId = new(BuildNameLookup);
    private static readonly Lazy<HashSet<uint>> StorableItemIds = new(() =>
        [.. Lumina.Excel.Sheets.Cabinet.Where(row => row.RowId > 0 && row.Item.RowId > 0).Select(row => row.Item.RowId)]);

    private static readonly Dictionary<string, uint> ResolvedNameCache = [];

    internal static bool IsStorableInCabinet(uint itemId) => StorableItemIds.Value.Contains(itemId);

    internal static bool TryGetItemId(string itemName, out uint itemId) {
        if (itemName.Length == 0) {
            itemId = 0;
            return false;
        }

        if (NameToItemId.Value.TryGetValue(itemName, out itemId))
            return true;

        if (ResolvedNameCache.TryGetValue(itemName, out itemId))
            return itemId != 0;

        foreach (var storableId in StorableItemIds.Value) {
            var row = Item.GetRow(storableId);
            if (!row.Name.IsEmpty && string.Equals(row.Name.ToString().Trim(), itemName, StringComparison.OrdinalIgnoreCase)) {
                ResolvedNameCache[itemName] = storableId;
                itemId = storableId;
                return true;
            }

            if (!row.Singular.IsEmpty && string.Equals(row.Singular.ToString().Trim(), itemName, StringComparison.OrdinalIgnoreCase)) {
                ResolvedNameCache[itemName] = storableId;
                itemId = storableId;
                return true;
            }
        }

        ResolvedNameCache[itemName] = 0;
        return false;
    }

    private static FrozenDictionary<string, uint> BuildNameLookup() {
        var dict = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Item.Where(i => i is { RowId: > 0, Name.IsEmpty: false, Singular.IsEmpty: false })) {
            dict.TryAdd(row.Name.ToString(), row.RowId);
            dict.TryAdd(row.Singular.ToString(), row.RowId);
        }
        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
