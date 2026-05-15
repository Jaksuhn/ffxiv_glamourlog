using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

internal static unsafe class GuideNavRowClick {
    internal static void Wire(ListButtonNode button, System.Action onClick) {
        button.OnClick = null;
        // native path; matches LogWindow category list (not ButtonClick alone)
        button.AddEvent(AtkEventType.MouseClick, (_, _, _, _, atkEventData) => {
            if (atkEventData == null)
                return;
            ref var eventData = ref *atkEventData;
            if (!eventData.IsLeftClick)
                return;
            onClick();
        });
    }

    internal static void ClearClickHandlers(this ListButtonNode button) {
        button.OnClick = null;
        button.RemoveEvent(AtkEventType.MouseClick);
    }
}
