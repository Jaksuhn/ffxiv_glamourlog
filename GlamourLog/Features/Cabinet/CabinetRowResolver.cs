using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourLog.Features.Cabinet;

internal static unsafe class CabinetRowResolver {
    private const uint _itemNameTextNodeId = 3;

    internal static bool TryResolve(AtkComponentListItemRenderer* renderer, AtkComponentListItemPopulator.ListItemInfo* itemInfo, out CabinetItemRenderer context) {
        context = default;

        if (itemInfo is not null && itemInfo->ListItem is not null) {
            var listItem = itemInfo->ListItem;

            foreach (var raw in listItem->UIntValues) {
                var baseId = ItemUtil.GetBaseId(raw).ItemId;
                if (baseId == 0)
                    continue;
                if (!CabinetItemNameLookup.IsStorableInCabinet(baseId))
                    continue;

                context = new CabinetItemRenderer {
                    ItemId = baseId,
                    IsStorable = true,
                    ItemName = null,
                };
                return true;
            }

            foreach (var label in listItem->StringValues) {
                var name = label.ToString().Trim();
                if (CabinetItemNameLookup.TryGetItemId(name, out var itemId)) {
                    context = new CabinetItemRenderer {
                        ItemId = itemId,
                        IsStorable = true,
                        ItemName = name,
                    };
                    return true;
                }
            }

            if (renderer is null)
                renderer = listItem->Renderer;
        }

        if (renderer is null)
            return false;

        var itemName = GetItemName(renderer);
        if (string.IsNullOrWhiteSpace(itemName))
            return false;

        if (!CabinetItemNameLookup.TryGetItemId(itemName, out var fromName))
            return false;

        context = new CabinetItemRenderer {
            ItemId = fromName,
            IsStorable = true,
            ItemName = itemName,
        };
        return true;
    }

    internal static string? GetItemName(AtkComponentListItemRenderer* renderer) {
        if (renderer is null)
            return null;

        var textNode = FindTextNodeById(&renderer->UldManager, _itemNameTextNodeId);
        return textNode is null ? null : ReadText(textNode);
    }

    private static AtkTextNode* FindTextNodeById(AtkUldManager* uld, uint nodeId) {
        if (uld->NodeList is null || uld->NodeListCount == 0)
            return null;

        for (var i = 0u; i < uld->NodeListCount; i++) {
            var node = uld->NodeList[i];
            if (node is null)
                continue;

            if (node->NodeId == nodeId && node->Type == NodeType.Text)
                return (AtkTextNode*)node;

            if (node->Type != NodeType.Component)
                continue;

            var child = (AtkComponentNode*)node;
            if (child->Component is null)
                continue;

            var found = FindTextNodeById(&child->Component->UldManager, nodeId);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static string? ReadText(AtkTextNode* textNode) {
        try {
            var text = textNode->GetText().ExtractText().Trim();
            return text.Length == 0 ? null : text;
        }
        catch {
            try {
                var text = textNode->NodeText.ToString().Trim();
                return text.Length == 0 ? null : text;
            }
            catch {
                return null;
            }
        }
    }
}
