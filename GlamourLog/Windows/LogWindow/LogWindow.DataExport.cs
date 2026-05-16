using Dalamud.Bindings.ImGui;
using GlamourLog.Services;

namespace GlamourLog;

internal partial class LogWindow {
    private void OnDataExportFormatSelected(GlamourDataExportFormat format) {
        if (format is not GlamourDataExportFormat.LalaAchievements)
            return;

        var json = GlamourDataExport.BuildLalaAchievementsJson(Svc.Get<OwnershipService>());
        ImGui.SetClipboardText(json);
    }
}
