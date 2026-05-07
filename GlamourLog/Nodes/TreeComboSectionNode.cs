using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GlamourLog.Nodes;

/// <summary>Tree category node + optional primary journal (<see cref="JournalHeader"/>).</summary>
public sealed class TreeComboSectionNode : TreeListCategoryNode {
    public TreeListHeaderNode? JournalHeader { get; }

    public TreeComboSectionNode(string panelTitle, string journalTitle, float listWidth)
        : this(panelTitle, journalTitle, listWidth, includeJournalHeader: true) { }

    public TreeComboSectionNode(string panelTitle, float listWidth)
        : this(panelTitle, string.Empty, listWidth, includeJournalHeader: false) { }

    private TreeComboSectionNode(string panelTitle, string journalTitle, float listWidth, bool includeJournalHeader) {
        Width = listWidth;
        IsCollapsed = false;
        VerticalPadding = 2f;
        String = panelTitle;
        LabelNode.TextColor = 1.Vec4();
        LabelNode.AddTextFlags(TextFlags.Emboss, TextFlags.Ellipsis);

        if (includeJournalHeader) {
            JournalHeader = new TreeListHeaderNode {
                Width = listWidth,
                Height = 24f,
                String = journalTitle,
            };
            JournalHeader.LabelNode.TextColor = new Vector4(0, 0, 0, 1);
            JournalHeader.LabelNode.Position = new Vector2(22f, 0f);
            JournalHeader.LabelNode.RemoveTextFlags(TextFlags.Emboss);
            AddNode(JournalHeader);
        }
    }

    /// <summary> Sets <see cref="JournalHeader"/> when present; when <paramref name="subsectionCount"/> > 0 and <paramref name="countUnitPlural"/> is non-empty, appends <c> (count unit)</c>.</summary>
    public void SetJournal(string baseJournalLine, int subsectionCount = 0, string countUnitPlural = "") {
        JournalHeader?.String = subsectionCount > 0 && countUnitPlural.Length > 0 ? $"{baseJournalLine} ({subsectionCount} {countUnitPlural})" : baseJournalLine;
    }
}
