using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using GlamourLog.Nodes.GuideWindow;
using System.Text.Json;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class DataExportGuidePage : IGuidePage {
    public string Id => "export.data";
    public GuideCategory Category => GuideCategory.Export;
    public int Order => 0;
    public string Title => "Data Export";

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context)
        => [
            new GuideTextBlock(
                new Lumina.Text.ReadOnly.ReadOnlySeString(
                    new SeStringBuilder()
                        .Append("Here you can export your dresser and armoire data into a format importable into websites and other plugins.")
                        .Encode())),
            new DataExportActionBlock(
                GlamourDataExportFormat.LalaAchievements,
                () => CopyDataExportToClipboard(context, GlamourDataExportFormat.LalaAchievements)),
        ];

    private static void CopyDataExportToClipboard(GuidePageContext context, GlamourDataExportFormat format) {
        if (format is not GlamourDataExportFormat.LalaAchievements)
            return;

        context.Ownership.BuildLalaExport(out var outfitsBySetId, out var armoireIds);
        var json = JsonSerializer.Serialize(new { outfits = outfitsBySetId, armoires = armoireIds });
        ImGui.SetClipboardText(json);
    }

    private sealed record DataExportActionBlock(GlamourDataExportFormat Format, System.Action OnCopy)
        : GuideBlock<DataExportRowNode> {
        protected override DataExportRowNode Create(float width) => new(width, Format, OnCopy);
        protected override void Relayout(DataExportRowNode node, float width) => node.Relayout(width);
    }
}
