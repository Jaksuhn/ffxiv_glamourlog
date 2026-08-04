using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourLog.Nodes;

internal sealed class SetListCurrencyFilterNode : ResNode {
    private const float DropDownHeight = 24f;
    private const int MaxVisibleOptions = 16;

    internal const float LayoutHeight = DropDownHeight;

    public DropDownNode<uint> DropDown { get; }

    public SetListCurrencyFilterNode(float width) {
        Size = new Vector2(width, DropDownHeight);

        DropDown = new DropDownNode<uint> {
            Position = Vector2.Zero,
            Size = new Vector2(width, DropDownHeight),
            MaxListOptions = MaxVisibleOptions,
            GetLabelFunction = LabelFor,
            Options = [NoneCurrencyId],
            SelectedOption = NoneCurrencyId,
        };
        DropDown.AttachNode(this);
    }

    internal const uint NoneCurrencyId = 0;

    internal void SyncOptions(IReadOnlyList<uint> currencyItemIds, uint selectedCurrencyItemId) {
        CollapseIfOpen(); // rebuild clears the nodes and if you do that while it's open you will cause an exception

        var options = new List<uint>(currencyItemIds.Count + 1) { NoneCurrencyId };
        options.AddRange(currencyItemIds);
        DropDown.Options = options;

        var selected = selectedCurrencyItemId != NoneCurrencyId && currencyItemIds.Contains(selectedCurrencyItemId) ? selectedCurrencyItemId : NoneCurrencyId;
        DropDown.SelectedOption = selected;
    }

    internal void Relayout(float width) {
        Size = new Vector2(width, DropDownHeight);
        DropDown.Size = new Vector2(width, DropDownHeight);
    }

    internal void CollapseIfOpen() {
        if (!DropDown.IsCollapsed)
            DropDown.Collapse(playSoundEffect: false);
    }

    private static ReadOnlySeString LabelFor(uint currencyItemId)
        => currencyItemId == NoneCurrencyId ? "All currencies" : Item.GetRow(currencyItemId).Name;
}
