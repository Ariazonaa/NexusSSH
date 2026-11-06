using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.Tests;

public class SplitTreeTests
{
    /// <summary>Schreibt einen Baum als Text: <c>[a|b]</c> nebeneinander, <c>[a/b]</c> untereinander.</summary>
    private static string Show(SplitNode<string>? node) => node switch
    {
        null => "-",
        SplitLeaf<string> leaf => leaf.Item,
        SplitBranch<string> branch => "[" + string.Join(branch.SideBySide ? "|" : "/", branch.Children.Select(Show)) + "]",
        _ => "?",
    };

    private static SplitNode<string> Row(params string[] items) => Row2(true, items);

    private static SplitNode<string> Row2(bool sideBySide, params string[] items) =>
        new SplitBranch<string>(sideBySide, [.. items.Select(item => (SplitNode<string>)new SplitLeaf<string>(item))],
            [.. items.Select(_ => 1.0 / items.Length)]);

    private static SplitNode<string> Leaf(string item) => new SplitLeaf<string>(item);

    private static SplitNode<string> Branch(bool sideBySide, params SplitNode<string>[] children) =>
        new SplitBranch<string>(sideBySide, children, [.. children.Select(_ => 1.0 / children.Length)]);

    [Fact]
    public void Splitting_beside_a_pane_of_the_same_direction_adds_a_sibling()
    {
        var tree = SplitTree.Insert(Row("a", "b"), "b", "c", sideBySide: true);

        Assert.Equal("[a|b|c]", Show(tree));
        Assert.Equal(new[] { 0.5, 0.25, 0.25 }, ((SplitBranch<string>)tree).Shares);
    }

    [Fact]
    public void Splitting_a_single_pane_makes_a_branch()
    {
        Assert.Equal("[a/b]", Show(SplitTree.Insert(Leaf("a"), "a", "b", sideBySide: false)));
        Assert.Equal("b", Show(SplitTree.Insert(null, "x", "b", sideBySide: true)));
    }

    [Fact]
    public void Splitting_across_wraps_the_pane_in_a_branch_of_its_own()
    {
        var tree = SplitTree.Insert(Row("a", "b"), "b", "c", sideBySide: false);

        Assert.Equal("[a|[b/c]]", Show(tree));
        Assert.Equal(new[] { 0.5, 0.5 }, ((SplitBranch<string>)tree).Shares);
    }

    [Fact]
    public void Removing_the_last_but_one_child_collapses_and_merges_the_same_direction()
    {
        // [a|[b/c]] ohne c: [b] fällt weg, b steht neben a
        Assert.Equal("[a|b]", Show(SplitTree.Remove(SplitTree.Insert(Row("a", "b"), "b", "c", false), "c")));

        // [x/[a|b]] mit einem Dritten daneben: [[a|b]|c] ohne c ⇒ [a|b]
        var nested = Branch(true, Branch(false, Row("a", "b"), Leaf("d")), Leaf("c"));
        Assert.Equal("[[a|b]/d]", Show(SplitTree.Remove(nested, "c")));

        // Gleiche Richtung wie der Elternzweig: [a|[b/[c|d]]] ohne b ⇒ [a|[c|d]] ⇒ [a|c|d], Anteile multipliziert
        var deep = new SplitBranch<string>(true,
            [Leaf("a"), new SplitBranch<string>(false, [Leaf("b"), Row("c", "d")], [0.5, 0.5])], [0.4, 0.6]);
        var merged = SplitTree.Remove(deep, "b");
        Assert.Equal("[a|c|d]", Show(merged));
        var shares = ((SplitBranch<string>)merged!).Shares;
        Assert.Equal(0.4, shares[0], 9);
        Assert.Equal(0.3, shares[1], 9);
        Assert.Equal(0.3, shares[2], 9);
    }

    [Fact]
    public void Removing_the_only_pane_leaves_nothing_and_unknown_panes_change_nothing()
    {
        Assert.Null(SplitTree.Remove(Leaf("a"), "a"));
        var tree = Row("a", "b");
        Assert.Same(tree, SplitTree.Remove(tree, "z"));
    }

    [Fact]
    public void Removing_a_pane_hands_its_space_to_the_rest_in_proportion()
    {
        var tree = new SplitBranch<string>(true, [Leaf("a"), Leaf("b"), Leaf("c")], [0.5, 0.25, 0.25]);
        var rest = (SplitBranch<string>)SplitTree.Remove(tree, "c")!;

        Assert.Equal(2.0 / 3, rest.Shares[0], 9);
        Assert.Equal(1.0 / 3, rest.Shares[1], 9);
    }

    [Fact]
    public void Moving_a_pane_to_an_edge_puts_it_there()
    {
        Assert.Equal("[b|[c/a]]", Show(SplitTree.Move(Row("a", "b", "c"), "a", "c", PaneSide.Bottom)));
        Assert.Equal("[a|c|b]", Show(SplitTree.Move(Row("a", "b", "c"), "c", "a", PaneSide.Right)));
        Assert.Equal("[c|a|b]", Show(SplitTree.Move(Row("a", "b", "c"), "c", "a", PaneSide.Left)));
        Assert.Equal("[[c/a]|b]", Show(SplitTree.Move(Row("a", "b", "c"), "c", "a", PaneSide.Top)));
    }

    [Fact]
    public void Moving_onto_itself_or_to_something_unknown_changes_nothing()
    {
        var tree = Row("a", "b");
        Assert.Same(tree, SplitTree.Move(tree, "a", "a", PaneSide.Left));
        Assert.Same(tree, SplitTree.Move(tree, "a", "z", PaneSide.Left));
        Assert.Same(tree, SplitTree.Move(tree, "z", "a", PaneSide.Left));
    }

    [Fact]
    public void Shares_from_the_page_are_checked_before_they_count()
    {
        var tree = Row("a", "b");

        Assert.Null(SplitTree.WithShares(tree, [], [double.NaN, 0.5]));
        Assert.Null(SplitTree.WithShares(tree, [], [0, 1]));
        Assert.Null(SplitTree.WithShares(tree, [], [0.99, 0.01]));
        Assert.Null(SplitTree.WithShares(tree, [], [0.5, 0.25, 0.25]));
        Assert.Null(SplitTree.WithShares(tree, [], [0.5, 0.4]));
        Assert.Null(SplitTree.WithShares(tree, [0], [0.5, 0.5]));      // Pfad ins Blatt
        Assert.Null(SplitTree.WithShares(tree, [5], [0.5, 0.5]));
        Assert.Null(SplitTree.WithShares(tree, [-1], [0.5, 0.5]));

        var ok = SplitTree.WithShares(tree, [], [0.3, 0.7]);
        Assert.Equal(new[] { 0.3, 0.7 }, ((SplitBranch<string>)ok!).Shares);

        // Summe knapp daneben wird normalisiert
        var near = (SplitBranch<string>)SplitTree.WithShares(tree, [], [0.3, 0.705])!;
        Assert.Equal(1.0, near.Shares.Sum(), 9);
    }

    [Fact]
    public void Shares_can_be_set_deep_in_the_tree()
    {
        var tree = SplitTree.Insert(Row("a", "b"), "b", "c", sideBySide: false);
        var changed = SplitTree.WithShares(tree, [1], [0.2, 0.8])!;

        var inner = (SplitBranch<string>)((SplitBranch<string>)changed).Children[1];
        Assert.Equal(new[] { 0.2, 0.8 }, inner.Shares);
        Assert.Equal(new[] { 0.5, 0.5 }, ((SplitBranch<string>)changed).Shares);
    }

    [Fact]
    public void Equalize_gives_every_branch_equal_shares()
    {
        var tree = new SplitBranch<string>(true, [Leaf("a"), new SplitBranch<string>(false, [Leaf("b"), Leaf("c")], [0.2, 0.8]), Leaf("d")],
            [0.1, 0.5, 0.4]);
        var even = (SplitBranch<string>)SplitTree.Equalize(tree);

        Assert.All(even.Shares, share => Assert.Equal(1.0 / 3, share, 9));
        Assert.Equal(new[] { 0.5, 0.5 }, ((SplitBranch<string>)even.Children[1]).Shares);
    }

    [Fact]
    public void The_neighbour_is_the_pane_that_shares_the_most_border()
    {
        // links a (ganze Höhe), rechts oben b, rechts mitte c (klein), rechts unten d – c ragt nach links nicht hinaus.
        // Dazu e unter allem als L: [[a|[b/c/d]]/e]
        var right = new SplitBranch<string>(false, [Leaf("b"), Leaf("c"), Leaf("d")], [0.5, 0.1, 0.4]);
        var top = new SplitBranch<string>(true, [Leaf("a"), right], [0.5, 0.5]);
        var tree = new SplitBranch<string>(false, [top, Leaf("e")], [0.8, 0.2]);

        // Von a nach rechts: b überdeckt 0.5, c 0.1, d 0.4 (der oberen Reihe, Höhe 0.8) ⇒ b
        Assert.Equal("b", SplitTree.Neighbor(tree, "a", PaneDirection.Right));
        Assert.Equal("a", SplitTree.Neighbor(tree, "b", PaneDirection.Left));
        Assert.Equal("a", SplitTree.Neighbor(tree, "c", PaneDirection.Left));
        Assert.Equal("a", SplitTree.Neighbor(tree, "d", PaneDirection.Left));
        // Nach unten: von a nach e, von d nach e; von b nach c
        Assert.Equal("e", SplitTree.Neighbor(tree, "a", PaneDirection.Down));
        Assert.Equal("e", SplitTree.Neighbor(tree, "d", PaneDirection.Down));
        Assert.Equal("c", SplitTree.Neighbor(tree, "b", PaneDirection.Down));
        Assert.Equal("b", SplitTree.Neighbor(tree, "c", PaneDirection.Up));
        // Von e nach oben: a überdeckt 0.5 der Breite, die rechte Spalte (d) ebenfalls 0.5 ⇒ Gleichstand, links gewinnt
        Assert.Equal("a", SplitTree.Neighbor(tree, "e", PaneDirection.Up));
        // Kein Nachbar
        Assert.Null(SplitTree.Neighbor(tree, "a", PaneDirection.Left));
        Assert.Null(SplitTree.Neighbor(tree, "e", PaneDirection.Down));
        Assert.Null(SplitTree.Neighbor(tree, "z", PaneDirection.Up));
    }

    [Fact]
    public void The_neighbour_with_the_larger_overlap_wins_over_the_upper_one()
    {
        // links a, rechts oben b (0.3), rechts unten c (0.7): von a nach rechts ⇒ c überdeckt mehr
        var tree = new SplitBranch<string>(true,
            [Leaf("a"), new SplitBranch<string>(false, [Leaf("b"), Leaf("c")], [0.3, 0.7])], [0.5, 0.5]);

        Assert.Equal("c", SplitTree.Neighbor(tree, "a", PaneDirection.Right));
    }

    [Fact]
    public void Old_saved_shapes_open_as_before()
    {
        Assert.Equal("[a|b]", Show(SplitTree.FromFlat(["a", "b"], sideBySide: true)));
        Assert.Equal("[a/b]", Show(SplitTree.FromFlat(["a", "b"], sideBySide: false)));
        Assert.Equal("[[a|b]/c]", Show(SplitTree.FromFlat(["a", "b", "c"], sideBySide: true)));
        Assert.Equal("[[a|b]/c]", Show(SplitTree.FromFlat(["a", "b", "c"], sideBySide: false)));
        Assert.Equal("[[a|b]/[c|d]]", Show(SplitTree.FromFlat(["a", "b", "c", "d"], sideBySide: true)));
        Assert.Equal(new[] { "a", "b", "c", "d" }, SplitTree.Leaves(SplitTree.FromFlat(["a", "b", "c", "d"], true)!));
        Assert.True(SplitTree.IsWellFormed(SplitTree.FromFlat(["a", "b", "c", "d"], true)));
    }

    [Fact]
    public void Saved_groups_that_make_no_tree_give_nothing()
    {
        Assert.Null(SplitTree.FromFlat<string>(null, true));
        Assert.Null(SplitTree.FromFlat(Array.Empty<string>(), true));
        Assert.Null(SplitTree.FromFlat(["a"], true));
        Assert.Null(SplitTree.FromFlat(Enumerable.Range(0, SplitTree.MaxPanes + 1).Select(i => "p" + i).ToList(), true));
    }

    [Fact]
    public void Leaves_run_from_top_left_to_bottom_right_and_map_keeps_the_shape()
    {
        var tree = SplitTree.Insert(Row("a", "b"), "b", "c", sideBySide: false);

        Assert.Equal(new[] { "a", "b", "c" }, SplitTree.Leaves(tree));
        var mapped = SplitTree.Map(tree, item => item.ToUpperInvariant());
        Assert.Equal("[A|[B/C]]", Show(mapped));
        Assert.Equal(((SplitBranch<string>)tree).Shares, ((SplitBranch<string>)mapped).Shares);
    }

    [Fact]
    public void More_than_sixteen_panes_or_a_deeper_tree_is_refused()
    {
        SplitNode<string> tree = Leaf("p0");
        for (var i = 1; i < SplitTree.MaxPanes; i++)
        {
            tree = SplitTree.Insert(tree, "p" + (i - 1), "p" + i, sideBySide: true);
        }
        Assert.Equal(SplitTree.MaxPanes, SplitTree.Leaves(tree).Count);
        Assert.Same(tree, SplitTree.Insert(tree, "p0", "extra", sideBySide: false));

        // Tiefe: immer abwechselnd im jüngsten Blatt teilen
        SplitNode<string> deep = Leaf("d0");
        var added = 0;
        for (var i = 1; i <= SplitTree.MaxDepth + 3; i++)
        {
            var next = SplitTree.Insert(deep, "d" + (i - 1), "d" + i, sideBySide: i % 2 == 1);
            if (ReferenceEquals(next, deep))
            {
                break;
            }
            deep = next;
            added = i;
        }
        // Eine Kette mit n Zweigen braucht n + 1 Bereiche: Vor der Tiefe greift die Grenze der Bereiche.
        Assert.Equal(SplitTree.MaxPanes - 1, added);
        Assert.Same(deep, SplitTree.Insert(deep, "d" + added, "more", sideBySide: added % 2 == 0));
    }

    [Fact]
    public void Adding_a_pane_that_is_already_there_changes_nothing()
    {
        var tree = Row("a", "b");
        Assert.Same(tree, SplitTree.Insert(tree, "a", "b", sideBySide: true));
        Assert.Same(tree, SplitTree.Insert(tree, "z", "c", sideBySide: true));
    }

    [Fact]
    public void Moving_next_to_a_pane_in_a_branch_of_the_same_direction_joins_that_branch()
    {
        Assert.Equal("[b/c/a]", Show(SplitTree.Move(Branch(true, Leaf("a"), Branch(false, Leaf("b"), Leaf("c"))), "a", "c", PaneSide.Bottom)));
    }

    private static SplitNode<string> Chain(int branches)
    {
        SplitNode<string> node = Leaf("x0");
        for (var i = 1; i <= branches; i++)
        {
            node = new SplitBranch<string>(i % 2 == 0, [node, Leaf("x" + i)], [0.5, 0.5]);
        }
        return node;
    }

    [Fact]
    public void A_tree_is_well_formed_only_with_every_rule_kept()
    {
        Assert.True(SplitTree.IsWellFormed(Row("a", "b", "c")));
        Assert.True(SplitTree.IsWellFormed(Leaf("a")));
        Assert.False(SplitTree.IsWellFormed<string>(null));

        SplitNode<string> Shares(params double[] shares) =>
            new SplitBranch<string>(true, [Leaf("a"), Leaf("b")], shares);

        Assert.False(SplitTree.IsWellFormed(Shares(double.PositiveInfinity, 0.5)));
        Assert.False(SplitTree.IsWellFormed(Shares(double.NegativeInfinity, 0.5)));
        Assert.False(SplitTree.IsWellFormed(Shares(double.NaN, 0.5)));
        Assert.False(SplitTree.IsWellFormed(Shares()));
        Assert.False(SplitTree.IsWellFormed(Shares(1.0)));
        Assert.False(SplitTree.IsWellFormed(Shares(0.5, 0.25, 0.25)));
        Assert.False(SplitTree.IsWellFormed(Shares(0.01, 0.99)));
        Assert.False(SplitTree.IsWellFormed(Shares(0.4, 0.4)));
        Assert.True(SplitTree.IsWellFormed(Shares(0.3, 0.705)));

        Assert.False(SplitTree.IsWellFormed(new SplitBranch<string>(true, [], [])));
        Assert.False(SplitTree.IsWellFormed(new SplitBranch<string>(true, [Leaf("a")], [1.0])));
        Assert.False(SplitTree.IsWellFormed(new SplitBranch<string>(true, [Leaf("a"), null!], [0.5, 0.5])));
        Assert.False(SplitTree.IsWellFormed(new SplitBranch<string>(true, null!, null!)));

        Assert.False(SplitTree.IsWellFormed(Row("a", "b", "a")));
        Assert.False(SplitTree.IsWellFormed(Branch(true, Leaf("a"), Branch(false, Leaf("b"), Leaf("a")))));
    }

    [Fact]
    public void A_tree_with_too_many_panes_or_too_deep_is_not_well_formed()
    {
        Assert.True(SplitTree.IsWellFormed(Chain(SplitTree.MaxPanes - 1)));   // 15 Zweige, 16 Bereiche
        Assert.False(SplitTree.IsWellFormed(Chain(SplitTree.MaxDepth)));
        Assert.False(SplitTree.IsWellFormed(Chain(SplitTree.MaxDepth + 1)));
        Assert.False(SplitTree.IsWellFormed(Chain(10000)));   // läuft nicht ins Leere hinab

        Assert.True(SplitTree.IsWellFormed(Row([.. Enumerable.Range(0, SplitTree.MaxPanes).Select(i => "p" + i)])));
        Assert.False(SplitTree.IsWellFormed(Row([.. Enumerable.Range(0, SplitTree.MaxPanes + 1).Select(i => "p" + i)])));
    }
}
