using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes.GuideWindow;

namespace GlamourLog.Windows.GuideWindow;

internal sealed class LogWindowSettingsGuidePage : IGuidePage {
    public string Id => "settings.log-window";
    public GuideCategory Category => GuideCategory.Settings;
    public int Order => 0;
    public string Title => "Glamour Log Window";

    public IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context) {
        var configuration = context.Configuration;
        return [
            new CheckboxSettingBlock(
                "Disable force window closing",
                "Prevents the game from closing the addon when you go through an area transition or cutscene. Will still hide the addon. Also disables the ability to close via ESC. Must be clicked manually.",
                () => configuration.DisableClose,
                value => SetDisableClose(context, value)),
            new CheckboxSettingBlock(
                "Persist search",
                "Keeps the search text when the Glamour Log window is closed and reopened.",
                () => configuration.PersistSearch,
                value => {
                    configuration.PersistSearch = value;
                    configuration.Save();
                }),
        ];
    }

    private static unsafe void SetDisableClose(GuidePageContext context, bool value) {
        var configuration = context.Configuration;
        configuration.DisableClose = value;
        configuration.Save();

        AtkUnitBase* addon = context.Windows.LogWindow;
        if (addon is not null)
            addon->ShouldFireCallbackAndHideOrClose = value;
    }

    private sealed record CheckboxSettingBlock(string Label, string InfoTooltip, Func<bool> Read, Action<bool> Write) : GuideBlock<ConfigCheckboxRowNode> {
        protected override ConfigCheckboxRowNode Create(float width)
            => new(width, Label, InfoTooltip, Read, Write);

        protected override void Relayout(ConfigCheckboxRowNode node, float width)
            => node.Relayout(width);
    }
}
