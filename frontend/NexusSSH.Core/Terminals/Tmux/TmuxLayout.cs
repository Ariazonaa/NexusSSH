namespace NexusSSH.Core.Terminals.Tmux;

/// <summary>Ein Bereich eines tmux-Fensters: Kennung, ob er der aktive ist, und wo er im Fenster liegt (in Zeichen).</summary>
public sealed record TmuxPaneInfo(string Id, bool Active, int Left, int Top, int Width, int Height);

/// <summary>Ein Fenster einer tmux-Sitzung mit seinen Bereichen, wie tmux sie nennt.</summary>
public sealed record TmuxWindowInfo(string Id, string Name, IReadOnlyList<TmuxPaneInfo> Panes)
{
    /// <summary>Der aktive Bereich – der, in den bei tmux getippt würde.</summary>
    public TmuxPaneInfo? ActivePane => Panes.FirstOrDefault(pane => pane.Active) ?? Panes.FirstOrDefault();
}

/// <summary>
/// Die Bereiche eines Fensters von tmux als Baum der Teilung. tmux teilt jedes Fenster schrittweise in zwei, die
/// Rechtecke danach nennen aber nur Lage und Größe in Zeichen – daraus wird der Baum wieder zerlegt: an
/// durchgehenden Schnitten (eine Zelle Trennlinie dazwischen), erst senkrecht, dann waagerecht, in jedem Stück von
/// vorn. Die Anteile ergeben sich aus den Breiten bzw. Höhen. Geometrie wird nie aus Layout-Zeichenketten gelesen:
/// die Rechtecke kommen von <c>list-panes</c>.
/// </summary>
public static class TmuxTree
{
    /// <summary>
    /// Der Baum der Bereiche; ein einzelner Bereich ist ein Blatt. <c>null</c> bei keinem oder mehr als
    /// <see cref="SplitTree.MaxPanes"/> Bereichen und wenn sich die Rechtecke nicht zerlegen lassen (Lücken,
    /// Überlappung, ein Anteil unter <see cref="SplitTree.MinShare"/>) – dann zeigt der Tab den aktiven Bereich allein.
    /// </summary>
    public static SplitNode<TmuxPaneInfo>? Of(IReadOnlyList<TmuxPaneInfo> panes)
    {
        if (panes.Count == 0 || panes.Count > SplitTree.MaxPanes)
        {
            return null;
        }

        var tree = Cut([.. panes]);
        return SplitTree.IsWellFormed(tree) ? tree : null;
    }

    private static SplitNode<TmuxPaneInfo>? Cut(List<TmuxPaneInfo> panes)
    {
        if (panes.Count == 1)
        {
            return new SplitLeaf<TmuxPaneInfo>(panes[0]);
        }

        foreach (var sideBySide in (bool[])[true, false])
        {
            var parts = Parts(panes, sideBySide);
            if (parts.Count < 2)
            {
                continue;
            }

            var children = new List<SplitNode<TmuxPaneInfo>>();
            foreach (var (part, _) in parts)
            {
                if (Cut(part) is not { } child)
                {
                    return null;
                }

                children.Add(child);
            }

            var total = parts.Sum(part => part.Extent);
            return new SplitBranch<TmuxPaneInfo>(sideBySide, children, [.. parts.Select(part => (double)part.Extent / total)]);
        }

        return null;
    }

    // Die Gruppen, in die die Schnitte quer zur Richtung die Bereiche zerlegen: Ein Schnitt liegt dort, wo alle
    // bisherigen Bereiche enden und der nächste genau eine Zelle (die Trennlinie) weiter beginnt.
    private static List<(List<TmuxPaneInfo> Panes, int Extent)> Parts(List<TmuxPaneInfo> panes, bool sideBySide)
    {
        int Start(TmuxPaneInfo pane) => sideBySide ? pane.Left : pane.Top;
        int End(TmuxPaneInfo pane) => Start(pane) + (sideBySide ? pane.Width : pane.Height);
        int Across(TmuxPaneInfo pane) => sideBySide ? pane.Top : pane.Left;

        var parts = new List<(List<TmuxPaneInfo>, int)>();
        var current = new List<TmuxPaneInfo>();
        int begin = 0, end = 0;
        foreach (var pane in panes.OrderBy(Start).ThenBy(Across))
        {
            if (current.Count > 0 && Start(pane) == end + 1)
            {
                parts.Add((current, end - begin));
                current = [];
            }

            if (current.Count == 0)
            {
                begin = Start(pane);
                end = End(pane);
            }
            else
            {
                end = Math.Max(end, End(pane));
            }

            current.Add(pane);
        }

        parts.Add((current, end - begin));
        return parts;
    }
}
