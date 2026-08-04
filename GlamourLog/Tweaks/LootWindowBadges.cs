using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourLog.Nodes;
using GlamourLog.Services;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using System.Threading.Tasks;

namespace GlamourLog.Tweaks;

// https://github.com/MidoriKami/VanillaPlus/blob/7d5490ffc59f4ccc56ef9ab22c99430941b97302/VanillaPlus/Features/EnhancedLootWindow/EnhancedLootWindow.cs
// basically the same as vp but with different icons and rules
internal sealed class LootWindowBadges : IPluginService, IAsyncDisposable {
    public int InitOrder => 15;

    private readonly AddonController<AddonNeedGreed> _addonController;
    private readonly List<GlamourIconNode> _badges = [];
    private readonly List<nint> _attachedIconNodes = [];

    public unsafe LootWindowBadges() {
        _addonController = new AddonController<AddonNeedGreed> {
            AddonName = "NeedGreed",
            OnSetup = OnSetup,
            OnFinalize = OnFinalize,
            OnRefresh = OnRefresh,
        };
        IFramework.Get().Run(() => _addonController.Enable());
    }

    private unsafe void OnSetup(AddonNeedGreed* addon) {
        DisposeBadges();
        var count = GetNumItems(addon);
        SetupBadges(addon->NumItems);
        AttachBadges(addon);
    }

    private unsafe void OnFinalize(AddonNeedGreed* _) => DisposeBadges();

    private unsafe void OnRefresh(AddonNeedGreed* addon) {
        var count = GetNumItems(addon);
        SetupBadges(count);
        AttachBadges(addon);

        var catalog = CatalogService.Get();
        var ownership = OwnershipService.Get();
        var query = ownership.Query();

        for (var index = 0; index < count; index++) {
            var badge = _badges[index];
            ref var itemInfo = ref addon->Items[index];
            if (itemInfo.ItemId == 0 || _attachedIconNodes[index] == 0) {
                badge.IsVisible = false;
                continue;
            }

            var itemId = ItemUtil.GetBaseId(itemInfo.ItemId).ItemId;
            if (itemId == 0) {
                badge.IsVisible = false;
                continue;
            }

            // don't mark already owned
            if (query.Locate(itemId) is not PieceLocation.None) {
                badge.IsVisible = false;
                continue;
            }

            if (ownership.IsCabinetItem(itemId) || catalog.ArmoireItemIds.Contains(itemId)) {
                badge.SetPart(GlamourIconNode.IconPart.Armoire);
                badge.IsVisible = true;
                continue;
            }

            if (catalog.IsMirageOutfitPiece(itemId)) {
                badge.SetPart(GlamourIconNode.IconPart.Dresser);
                badge.IsVisible = true;
                continue;
            }

            badge.IsVisible = false;
        }

        for (var index = count; index < _badges.Count; index++)
            _badges[index].IsVisible = false;
    }

    private void SetupBadges(int count) {
        while (_badges.Count < count) {
            _badges.Add(new GlamourIconNode(GlamourIconNode.IconPart.Armoire) {
                Position = new Vector2(38f, 39f),
                IsVisible = false,
            });
            _attachedIconNodes.Add(0);
        }
    }

    private unsafe void AttachBadges(AddonNeedGreed* addon) {
        var list = GetList(addon);
        if (list is null)
            return;

        for (var index = 0; index < addon->NumItems; index++) {
            var renderer = list->GetItemRenderer(index);
            if (renderer is null) {
                _attachedIconNodes[index] = 0;
                continue;
            }

            var iconNode = renderer->UldManager.SearchNodeById(12);
            if (iconNode is null) {
                _attachedIconNodes[index] = 0;
                continue;
            }

            var iconPtr = (nint)iconNode;
            if (_attachedIconNodes[index] == iconPtr)
                continue;

            var badge = _badges[index];
            badge.DetachNode();
            badge.AttachNode(iconNode, NodePosition.AfterTarget);
            badge.Position = new Vector2(38f, 39f);
            badge.IsVisible = false;
            _attachedIconNodes[index] = iconPtr;
        }
    }

    private static unsafe int GetNumItems(AddonNeedGreed* addon) {
        var agent = AgentLoot.Instance();
        var numItems = agent is not null ? agent->NumItems : 0;
        return Math.Clamp(numItems, 0, addon->Items.Length);
    }

    private static unsafe AtkComponentList* GetList(AddonNeedGreed* addon) {
        var listNode = (AtkComponentNode*)addon->GetNodeById(6);
        return listNode is not null ? (AtkComponentList*)listNode->Component : null;
    }

    private void DisposeBadges() {
        foreach (var badge in _badges)
            badge.Dispose();
        _badges.Clear();
        _attachedIconNodes.Clear();
    }

    public async ValueTask DisposeAsync() {
        await Svc.Framework.RunOnFrameworkThread(() => {
            _addonController.Dispose();
            DisposeBadges();
        });
    }
}
