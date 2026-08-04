using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using GlamourLog.Services;
using System.Text.Json;

namespace GlamourLog.Windows.GuideWindow;

public partial class GuideWindow {
    private static readonly Page ExportData = new() {
        CategoryTitle = "Export",
        SubCategoryTitle = "Data Export",
        Blocks =
        [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("Here you can export your dresser and armoire data into a format importable into websites and other plugins.")
                        .Encode())),
            new DataExportActionBlock(GlamourDataExportFormat.LalaAchievements),
        ],
    };

    private static void CopyDataExportToClipboard(GlamourDataExportFormat format) {
        if (format is not GlamourDataExportFormat.LalaAchievements)
            return;

        OwnershipService.Get().BuildLalaExport(out var outfitsBySetId, out var armoireIds);
        var json = JsonSerializer.Serialize(new { outfits = outfitsBySetId, armoires = armoireIds });
        ImGui.SetClipboardText(json);
    }
}
