using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Settings;

/// <summary>
/// Eine Teilung der Terminal-Fläche, wie sie in den Einstellungen steht: die Nummern der beteiligten Tabs in ihrer
/// Anordnung (<paramref name="Tabs"/>, die Bereiche von oben links nach unten rechts) und seit der Teilung in
/// beliebiger Anordnung der Baum samt Anteilen. Ohne <paramref name="Tree"/> – eine ältere Datei, oder einer, der
/// nicht passte – öffnet sich die Teilung in der alten Form (<see cref="SplitTree.FromFlat{T}"/>); eine ältere
/// Fassung der App liest nur <paramref name="Tabs"/>.
/// </summary>
public sealed record SavedSplit(List<int> Tabs, bool SideBySide, SavedSplitNode? Tree = null)
{
    /// <summary>
    /// Der gespeicherte Baum als Baum von Tab-Nummern – oder <c>null</c>, wenn keiner da ist oder er nicht gilt: nicht
    /// wohlgeformt (<see cref="SplitTree.IsWellFormed{T}"/>) oder mit anderen Bereichen, als <see cref="Tabs"/> nennt.
    /// Die Datei ist fremde Eingabe; ob die Nummern zu offenen Tabs gehören, sieht erst, wer sie öffnet.
    /// </summary>
    public SplitNode<int>? ToTree()
    {
        if (Tree?.ToNode(0) is not { } node || !SplitTree.IsWellFormed(node) || Tabs is null)
        {
            return null;
        }

        var leaves = SplitTree.Leaves(node);
        return leaves.Order().SequenceEqual(Tabs.Distinct().Order()) && Tabs.Distinct().Count() == Tabs.Count ? node : null;
    }

    /// <summary>Hält einen Baum, der nicht gilt, nicht fest; die Nummern der Tabs bleiben.</summary>
    internal SavedSplit Cleaned() => Tree is not null && ToTree() is null ? this with { Tree = null } : this;

    /// <summary>Die Teilung für die Einstellungen: der Baum samt den Nummern seiner Bereiche.</summary>
    public static SavedSplit Of(SplitNode<int> tree)
        => new([.. SplitTree.Leaves(tree)], tree is SplitBranch<int> branch ? branch.SideBySide : true, SavedSplitNode.From(tree));
}

/// <summary>
/// Ein Knoten des gespeicherten Baums: ein Bereich (<paramref name="Tab"/> ab 0, ohne Kinder) oder ein Zweig mit
/// <paramref name="Children"/> und ihren <paramref name="Shares"/>.
/// </summary>
public sealed record SavedSplitNode(int Tab = -1, bool SideBySide = true, List<double>? Shares = null, List<SavedSplitNode>? Children = null)
{
    internal static SavedSplitNode From(SplitNode<int> node) => node switch
    {
        SplitBranch<int> branch => new SavedSplitNode(SideBySide: branch.SideBySide, Shares: [.. branch.Shares], Children: [.. branch.Children.Select(From)]),
        SplitLeaf<int> leaf => new SavedSplitNode(Tab: leaf.Item),
        _ => new SavedSplitNode(),
    };

    // Tiefer als ein Baum je sein darf, wird gar nicht erst gelesen: Die Datei kann beliebig tief verschachtelt sein.
    internal SplitNode<int>? ToNode(int depth)
    {
        if (depth > SplitTree.MaxDepth)
        {
            return null;
        }

        if (Children is not { Count: > 0 })
        {
            return Tab >= 0 ? new SplitLeaf<int>(Tab) : null;
        }

        var children = new List<SplitNode<int>>();
        foreach (var child in Children)
        {
            if (child?.ToNode(depth + 1) is not { } node)
            {
                return null;
            }

            children.Add(node);
        }

        return new SplitBranch<int>(SideBySide, children, [.. Shares ?? []]);
    }
}
