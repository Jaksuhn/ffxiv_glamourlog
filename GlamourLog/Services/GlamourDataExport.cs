using System.Text.Json;

namespace GlamourLog.Services;

internal static class GlamourDataExport {
    internal static string BuildLalaAchievementsJson(OwnershipService ownership) {
        ownership.GetLalaAchievementsExportBuckets(out var outfitsBySetId, out var armoireIds);
        return JsonSerializer.Serialize(new { outfits = outfitsBySetId, armoires = armoireIds });
    }
}
