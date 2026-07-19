using Dalamud.Bindings.ImGui;
using GlamourLog.Services;
using System.Text.Json;

namespace GlamourLog;

internal partial class LogWindow {
    private void OnDataExportFormatSelected(GlamourDataExportFormat format) {
        if (format is not GlamourDataExportFormat.LalaAchievements)
            return;

        Svc.Get<OwnershipService>().BuildLalaExport(out var outfitsBySetId, out var armoireIds);
        var json = JsonSerializer.Serialize(new { outfits = outfitsBySetId, armoires = armoireIds });
        ImGui.SetClipboardText(json);
    }
}
