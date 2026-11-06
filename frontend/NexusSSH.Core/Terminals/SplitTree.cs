namespace NexusSSH.Core.Terminals;

/// <summary>Ein Knoten der Teilung: ein Bereich (Blatt) oder ein Zweig aus mehreren Knoten.</summary>
public abstract record SplitNode<T>;

/// <summary>Ein einzelner Bereich.</summary>
public sealed record SplitLeaf<T>(T Item) : SplitNode<T>;

/// <summary>
/// Mehrere Knoten nebeneinander (<paramref name="SideBySide"/>) oder untereinander. <paramref name="Shares"/> sind ihre
/// Anteile am Platz des Zweigs und ergeben zusammen 1. Ein Zweig hat mindestens zwei Kinder.
/// </summary>
public sealed record SplitBranch<T>(bool SideBySide, IReadOnlyList<SplitNode<T>> Children, IReadOnlyList<double> Shares)
    : SplitNode<T>;

/// <summary>An welche Kante eines Bereichs ein anderer gestellt wird.</summary>
public enum PaneSide { Left, Right, Top, Bottom }

/// <summary>In welche Richtung der Nachbar eines Bereichs gesucht wird.</summary>
public enum PaneDirection { Left, Right, Up, Down }

/// <summary>
/// Rechenwerk für den Baum der Teilungen: unveränderlich, jede Änderung liefert einen neuen Baum. Was nicht geht
/// (zu viele Bereiche, zu tief, unbekannter Bereich), lässt den Baum, wie er ist – es wirft nichts, denn die
/// Wünsche kommen aus der Oberfläche und von der Terminal-Seite. Alle Operationen erwarten einen Baum, den
/// <see cref="IsWellFormed{T}"/> gelten lässt – ein Baum von außen (Einstellungen) geht vorher dort hindurch.
/// </summary>
public static class SplitTree
{
    /// <summary>Mehr Bereiche zeigt kein Fenster.</summary>
    public const int MaxPanes = 16;

    /// <summary>Tiefer als so viele Zweige ineinander wird nicht geteilt.</summary>
    public const int MaxDepth = 16;

    /// <summary>Kleinster Anteil, den ein Kind eines Zweigs von Hand bekommen darf.</summary>
    public const double MinShare = 0.05;

    private const double Epsilon = 1e-9;

    /// <summary>Die Bereiche von oben links nach unten rechts.</summary>
    public static IReadOnlyList<T> Leaves<T>(SplitNode<T> root)
    {
        var list = new List<T>();
        Collect(root, list);
        return list;
    }

    /// <summary>Stellt <paramref name="added"/> neben <paramref name="beside"/> (dahinter bzw. darunter).</summary>
    public static SplitNode<T> Insert<T>(SplitNode<T>? root, T beside, T added, bool sideBySide) =>
        InsertAt(root, beside, added, sideBySide, before: false);

    /// <summary>
    /// Nimmt einen Bereich heraus. Bleibt in einem Zweig ein Kind, fällt der Zweig weg; hat der Rest dieselbe
    /// Richtung wie der Zweig darüber, verschmilzt er mit ihm (die Anteile werden multipliziert). Nichts bleibt:
    /// <c>null</c>.
    /// </summary>
    public static SplitNode<T>? Remove<T>(SplitNode<T> root, T item) =>
        Contains(root, item) ? RemoveCore(root, item) : root;

    /// <summary>Stellt einen Bereich an die gewählte Kante eines anderen.</summary>
    public static SplitNode<T> Move<T>(SplitNode<T> root, T item, T target, PaneSide side)
    {
        if (Same(item, target) || !Contains(root, item) || !Contains(root, target))
        {
            return root;
        }
        if (RemoveCore(root, item) is not { } rest)
        {
            return root;
        }
        var moved = Place(rest, target, item, side is PaneSide.Left or PaneSide.Right, side is PaneSide.Left or PaneSide.Top);
        return Depth(moved) > MaxDepth ? root : moved;
    }

    /// <summary>
    /// Neue Anteile für die Kinder des Zweigs am Pfad (Nummern der Kinder von der Wurzel an; leer = die Wurzel).
    /// <c>null</c>, wenn der Pfad kein Zweig ist, die Zahl nicht stimmt, ein Anteil außerhalb von
    /// [<see cref="MinShare"/>, 1 − <see cref="MinShare"/>] liegt oder die Summe nicht 1 ± 0,01 ist.
    /// </summary>
    public static SplitNode<T>? WithShares<T>(SplitNode<T> root, IReadOnlyList<int> path, IReadOnlyList<double> shares)
    {
        if (path is null || shares is null)
        {
            return null;
        }
        var sum = 0.0;
        foreach (var share in shares)
        {
            if (!(share >= MinShare && share <= 1 - MinShare))
            {
                return null;
            }
            sum += share;
        }
        if (Math.Abs(sum - 1) > 0.01)
        {
            return null;
        }
        return SetShares(root, path, 0, Normalized(shares, sum));
    }

    /// <summary>Alle Zweige geben ihren Kindern gleich viel Platz.</summary>
    public static SplitNode<T> Equalize<T>(SplitNode<T> root) => root switch
    {
        SplitBranch<T> branch => new SplitBranch<T>(branch.SideBySide, [.. branch.Children.Select(Equalize)],
            [.. branch.Children.Select(_ => 1.0 / branch.Children.Count)]),
        _ => root,
    };

    /// <summary>
    /// Der Bereich, der in der Richtung an <paramref name="from"/> grenzt. Gerechnet wird mit den Rechtecken im
    /// Einheitsquadrat: Es zählen die Bereiche, die mit der Kante anstoßen; es gewinnt der, der quer dazu am
    /// meisten überdeckt, bei Gleichstand der obere bzw. linke.
    /// </summary>
    public static T? Neighbor<T>(SplitNode<T> root, T from, PaneDirection direction)
    {
        var rects = new List<(T Item, double X, double Y, double W, double H)>();
        Layout(root, 0, 0, 1, 1, rects);
        var own = rects.FindIndex(r => Same(r.Item, from));
        if (own < 0)
        {
            return default;
        }
        var (_, ox, oy, ow, oh) = rects[own];

        T? best = default;
        var bestOverlap = 0.0;
        var bestPosition = 0.0;
        var found = false;
        foreach (var (item, x, y, w, h) in rects)
        {
            if (Same(item, from))
            {
                continue;
            }
            var touches = direction switch
            {
                PaneDirection.Right => Math.Abs(x - (ox + ow)) < Epsilon,
                PaneDirection.Left => Math.Abs(x + w - ox) < Epsilon,
                PaneDirection.Down => Math.Abs(y - (oy + oh)) < Epsilon,
                _ => Math.Abs(y + h - oy) < Epsilon,
            };
            if (!touches)
            {
                continue;
            }
            var horizontal = direction is PaneDirection.Left or PaneDirection.Right;
            var overlap = horizontal
                ? Math.Min(y + h, oy + oh) - Math.Max(y, oy)
                : Math.Min(x + w, ox + ow) - Math.Max(x, ox);
            if (overlap <= Epsilon)
            {
                continue;
            }
            var position = horizontal ? y : x;
            if (!found || overlap > bestOverlap + Epsilon
                || (Math.Abs(overlap - bestOverlap) <= Epsilon && position < bestPosition))
            {
                best = item;
                bestOverlap = overlap;
                bestPosition = position;
                found = true;
            }
        }
        return best;
    }

    /// <summary>Derselbe Baum mit anderen Inhalten.</summary>
    public static SplitNode<TOut> Map<T, TOut>(SplitNode<T> root, Func<T, TOut> map) => root switch
    {
        SplitLeaf<T> leaf => new SplitLeaf<TOut>(map(leaf.Item)),
        SplitBranch<T> branch => new SplitBranch<TOut>(branch.SideBySide, [.. branch.Children.Select(child => Map(child, map))],
            [.. branch.Shares]),
        _ => throw new ArgumentException("Unknown node", nameof(root)),
    };

    /// <summary>
    /// Die alten, flachen Gruppen als Baum: zwei in der gegebenen Richtung; drei als zwei nebeneinander und der
    /// dritte in ganzer Breite darunter; vier im Raster. Mehr als vier (kannte die alte Form nicht) alle in einer Reihe.
    /// <c>null</c> bei weniger als zwei oder mehr als <see cref="MaxPanes"/> Bereichen (die Liste kommt aus der Datei
    /// der Einstellungen). Doppelte Einträge prüft erst <see cref="IsWellFormed{T}"/>.
    /// </summary>
    public static SplitNode<T>? FromFlat<T>(IReadOnlyList<T>? items, bool sideBySide)
    {
        if (items is null || items.Count < 2 || items.Count > MaxPanes)
        {
            return null;
        }
        SplitNode<T> Leaf(int i) => new SplitLeaf<T>(items[i]);
        SplitNode<T> Even(bool side, params SplitNode<T>[] children) =>
            new SplitBranch<T>(side, children, [.. children.Select(_ => 1.0 / children.Length)]);

        return items.Count switch
        {
            2 => Even(sideBySide, Leaf(0), Leaf(1)),
            3 => Even(false, Even(true, Leaf(0), Leaf(1)), Leaf(2)),
            4 => Even(false, Even(true, Leaf(0), Leaf(1)), Even(true, Leaf(2), Leaf(3))),
            _ => Even(sideBySide, [.. Enumerable.Range(0, items.Count).Select(Leaf)]),
        };
    }

    /// <summary>
    /// Ob ein Baum so gebaut ist, wie die übrigen Operationen ihn erwarten: jeder Zweig mit mindestens zwei Kindern
    /// und ebenso vielen Anteilen, jeder Anteil endlich und in [<see cref="MinShare"/>, 1 − <see cref="MinShare"/>],
    /// Summe 1 ± 0,01, höchstens <see cref="MaxDepth"/> Zweige tief, höchstens <see cref="MaxPanes"/> Bereiche, kein
    /// Bereich doppelt. Wirft nie und steigt nie tiefer als <see cref="MaxDepth"/> + 1 hinab: Bäume aus der Datei der
    /// Einstellungen sind fremde Eingabe und gehen vor jedem Gebrauch hierdurch.
    /// </summary>
    public static bool IsWellFormed<T>(SplitNode<T>? root)
    {
        if (root is null)
        {
            return false;
        }
        var seen = new List<T>();
        return Check(root, 0, seen);
    }

    private static bool Check<T>(SplitNode<T> node, int depth, List<T> seen)
    {
        switch (node)
        {
            case SplitLeaf<T> leaf:
                if (seen.Count >= MaxPanes || seen.Any(other => Same(other, leaf.Item)))
                {
                    return false;
                }
                seen.Add(leaf.Item);
                return true;
            case SplitBranch<T> branch:
                if (depth >= MaxDepth || branch.Children is null || branch.Shares is null
                    || branch.Children.Count < 2 || branch.Shares.Count != branch.Children.Count)
                {
                    return false;
                }
                var sum = 0.0;
                foreach (var share in branch.Shares)
                {
                    if (!(share >= MinShare && share <= 1 - MinShare))
                    {
                        return false;
                    }
                    sum += share;
                }
                if (Math.Abs(sum - 1) > 0.01)
                {
                    return false;
                }
                return branch.Children.All(child => child is not null && Check(child, depth + 1, seen));
            default:
                return false;
        }
    }

    private static bool Same<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

    private static void Collect<T>(SplitNode<T> node, List<T> list)
    {
        switch (node)
        {
            case SplitLeaf<T> leaf:
                list.Add(leaf.Item);
                break;
            case SplitBranch<T> branch:
                foreach (var child in branch.Children)
                {
                    Collect(child, list);
                }
                break;
        }
    }

    private static bool Contains<T>(SplitNode<T> node, T item) => node switch
    {
        SplitLeaf<T> leaf => Same(leaf.Item, item),
        SplitBranch<T> branch => branch.Children.Any(child => Contains(child, item)),
        _ => false,
    };

    private static int Depth<T>(SplitNode<T> node) =>
        node is SplitBranch<T> branch ? 1 + branch.Children.Max(Depth) : 0;

    private static SplitNode<T> InsertAt<T>(SplitNode<T>? root, T beside, T added, bool sideBySide, bool before)
    {
        if (root is null)
        {
            return new SplitLeaf<T>(added);
        }
        if (!Contains(root, beside) || Contains(root, added) || Leaves(root).Count >= MaxPanes)
        {
            return root;
        }
        var result = Place(root, beside, added, sideBySide, before);
        return Depth(result) > MaxDepth ? root : result;
    }

    /// <summary>Setzt <paramref name="added"/> neben das Blatt <paramref name="beside"/>, das im Baum steht.</summary>
    private static SplitNode<T> Place<T>(SplitNode<T> node, T beside, T added, bool sideBySide, bool before)
    {
        if (node is SplitLeaf<T> leaf)
        {
            if (!Same(leaf.Item, beside))
            {
                return node;
            }
            SplitNode<T> fresh = new SplitLeaf<T>(added);
            return new SplitBranch<T>(sideBySide, before ? [fresh, node] : [node, fresh], [0.5, 0.5]);
        }

        var branch = (SplitBranch<T>)node;
        var children = new List<SplitNode<T>>(branch.Children);
        var shares = new List<double>(branch.Shares);
        for (var i = 0; i < children.Count; i++)
        {
            if (!Contains(children[i], beside))
            {
                continue;
            }
            if (children[i] is SplitLeaf<T> && branch.SideBySide == sideBySide)
            {
                var half = shares[i] / 2;
                shares[i] = half;
                shares.Insert(before ? i : i + 1, half);
                children.Insert(before ? i : i + 1, new SplitLeaf<T>(added));
            }
            else
            {
                children[i] = Place(children[i], beside, added, sideBySide, before);
            }
            break;
        }
        return new SplitBranch<T>(branch.SideBySide, children, shares);
    }

    private static SplitNode<T>? RemoveCore<T>(SplitNode<T> node, T item)
    {
        if (node is SplitLeaf<T> leaf)
        {
            return Same(leaf.Item, item) ? null : node;
        }

        var branch = (SplitBranch<T>)node;
        var children = new List<SplitNode<T>>();
        var shares = new List<double>();
        for (var i = 0; i < branch.Children.Count; i++)
        {
            var child = Contains(branch.Children[i], item) ? RemoveCore(branch.Children[i], item) : branch.Children[i];
            if (child is null)
            {
                continue;
            }
            children.Add(child);
            shares.Add(branch.Shares[i]);
        }
        return Build(branch.SideBySide, children, shares);
    }

    /// <summary>Baut einen Zweig aus Kindern und beliebigen Gewichten: normalisiert, verschmilzt, fällt bei einem Kind weg.</summary>
    private static SplitNode<T>? Build<T>(bool sideBySide, List<SplitNode<T>> children, List<double> weights)
    {
        if (children.Count == 0)
        {
            return null;
        }
        var sum = weights.Sum();
        var flat = new List<SplitNode<T>>();
        var flatShares = new List<double>();
        for (var i = 0; i < children.Count; i++)
        {
            var share = sum > 0 ? weights[i] / sum : 1.0 / children.Count;
            if (children[i] is SplitBranch<T> inner && inner.SideBySide == sideBySide)
            {
                for (var j = 0; j < inner.Children.Count; j++)
                {
                    flat.Add(inner.Children[j]);
                    flatShares.Add(share * inner.Shares[j]);
                }
            }
            else
            {
                flat.Add(children[i]);
                flatShares.Add(share);
            }
        }
        return flat.Count == 1 ? flat[0] : new SplitBranch<T>(sideBySide, flat, flatShares);
    }

    private static IReadOnlyList<double> Normalized(IReadOnlyList<double> shares, double sum) =>
        Math.Abs(sum - 1) < 1e-12 ? [.. shares] : [.. shares.Select(share => share / sum)];

    private static SplitNode<T>? SetShares<T>(SplitNode<T> node, IReadOnlyList<int> path, int index, IReadOnlyList<double> shares)
    {
        if (node is not SplitBranch<T> branch)
        {
            return null;
        }
        if (index == path.Count)
        {
            return shares.Count == branch.Children.Count ? branch with { Shares = shares } : null;
        }
        var step = path[index];
        if (step < 0 || step >= branch.Children.Count)
        {
            return null;
        }
        if (SetShares(branch.Children[step], path, index + 1, shares) is not { } changed)
        {
            return null;
        }
        var children = new List<SplitNode<T>>(branch.Children) { [step] = changed };
        return branch with { Children = children };
    }

    private static void Layout<T>(SplitNode<T> node, double x, double y, double w, double h,
        List<(T Item, double X, double Y, double W, double H)> rects)
    {
        switch (node)
        {
            case SplitLeaf<T> leaf:
                rects.Add((leaf.Item, x, y, w, h));
                break;
            case SplitBranch<T> branch:
                var offset = 0.0;
                for (var i = 0; i < branch.Children.Count; i++)
                {
                    var share = branch.Shares[i];
                    if (branch.SideBySide)
                    {
                        Layout(branch.Children[i], x + offset * w, y, w * share, h, rects);
                    }
                    else
                    {
                        Layout(branch.Children[i], x, y + offset * h, w, h * share, rects);
                    }
                    offset += share;
                }
                break;
        }
    }
}
