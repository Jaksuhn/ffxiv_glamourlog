using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GlamourLog.Services;

internal sealed class AllaganToolsIpc {
    private readonly ICallGateSubscriber<uint, bool, uint[], uint> _itemCountOwned;

    public AllaganToolsIpc() {
        _itemCountOwned = Svc.Interface.GetIpcSubscriber<uint, bool, uint[], uint>("AllaganTools.ItemCountOwned");
    }

    internal bool TryGetOwnedCount(uint itemId, out int count) {
        count = 0;
        if (_itemCountOwned.HasFunction) {
            count = (int)_itemCountOwned.InvokeFunc(itemId, true, [.. InventoryType.AllPlayer.Concat(InventoryType.Retainer).Select(t => (uint)t)]);
            return true;
        }
        return false;
    }
}
