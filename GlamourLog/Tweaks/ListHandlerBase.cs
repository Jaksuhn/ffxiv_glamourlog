using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features;

internal abstract partial class ListHandlerBase(uint emptyListTextNodeId, IRowFilter[] filters) {
    protected readonly uint EmptyListTextNodeId = emptyListTextNodeId;
    protected readonly IRowFilter[] Filters = filters;

    protected bool IsFilteringActive => Filters.Any(f => f.IsEnabled);

    // native refresh with rows clears this; we re-show after filtering to zero (and on PostUpdate while idle-empty)
    protected unsafe void SetEmptyListMessageVisible(AtkUnitBase* addon, bool visible) {
        if (addon is null)
            return;

        var node = (AtkResNode*)addon->GetTextNodeById(EmptyListTextNodeId);
        if (node is null)
            return;

        node->ToggleVisibility(visible);
        if (visible)
            node->NodeFlags |= NodeFlags.Visible;
        else
            node->NodeFlags &= ~NodeFlags.Visible;

        addon->UldManager.UpdateDrawNodeList();
    }
}
