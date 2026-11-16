using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

public sealed class MarqueeTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(4, 4, false)] // genau die Schwelle: noch ein Klick
    [InlineData(5, 0, true)]
    [InlineData(0, -5, true)]
    public void A_frame_begins_only_beyond_the_drag_distance(double dx, double dy, bool expected)
        => Assert.Equal(expected, Marquee.Begins(dx, dy));

    [Theory]
    [InlineData(false, 50, true)] // freie Fläche unter der letzten Zeile
    [InlineData(true, 20, false)] // auf dem Symbol
    [InlineData(true, 80, false)] // auf dem Namen
    [InlineData(true, 140, true)] // rechts vom Namen (Größe, Datum, Leerraum)
    [InlineData(true, 4, true)] // links vor dem Symbol
    public void Symbol_and_name_drag_the_entry_the_rest_of_the_row_draws_a_frame(bool onItem, double x, bool expected)
        => Assert.Equal(expected, Marquee.StartsHere(onItem, x, nameLeft: 12, nameRight: 120));

    [Theory]
    [InlineData(false, false, MarqueeMode.Replace)]
    [InlineData(true, false, MarqueeMode.Add)]
    [InlineData(false, true, MarqueeMode.Extend)]
    [InlineData(true, true, MarqueeMode.Add)]
    public void The_keys_choose_the_mode(bool control, bool shift, MarqueeMode expected)
        => Assert.Equal(expected, Marquee.ModeFor(control, shift));

    [Fact]
    public void The_frame_touches_every_row_it_reaches_in_either_direction()
    {
        // Zeilen zu 28, die erste bei 2: Zeile 0 = 2..30, Zeile 1 = 30..58, Zeile 2 = 58..86.
        Assert.Equal((0, 1), Marquee.Rows(10, 31, top: 2, pitch: 28, count: 10));
        Assert.Equal((0, 1), Marquee.Rows(31, 10, top: 2, pitch: 28, count: 10));
        Assert.Equal((1, 1), Marquee.Rows(35, 40, top: 2, pitch: 28, count: 10));
        Assert.Equal((2, 2), Marquee.Rows(58, 60, top: 2, pitch: 28, count: 10));
    }

    [Fact]
    public void The_frame_is_cut_to_the_rows_that_exist()
    {
        // Über die letzte Zeile hinaus in die freie Fläche und über die erste hinaus nach oben.
        Assert.Equal((3, 4), Marquee.Rows(100, 900, top: 0, pitch: 28, count: 5));
        Assert.Equal((0, 2), Marquee.Rows(-50, 60, top: 0, pitch: 28, count: 5));

        // Ganz in der freien Fläche oder ganz darüber trifft er nichts.
        Assert.Null(Marquee.Rows(200, 300, top: 0, pitch: 28, count: 5));
        Assert.Null(Marquee.Rows(-30, -10, top: 0, pitch: 28, count: 5));
    }

    [Fact]
    public void Without_rows_or_measure_nothing_is_touched()
    {
        Assert.Null(Marquee.Rows(0, 50, top: 0, pitch: 28, count: 0));
        Assert.Null(Marquee.Rows(0, 50, top: 0, pitch: 0, count: 5));
        Assert.Null(Marquee.Rows(0, double.NaN, top: 0, pitch: 28, count: 5));
        Assert.Null(Marquee.Rows(0, 50, top: 0, pitch: double.NaN, count: 5));
    }

    [Fact]
    public void Without_a_key_the_frame_replaces_the_selection()
        => Assert.Equal([3, 4, 5], Sorted(Marquee.Selection(MarqueeMode.Replace, [0, 9], (3, 5), anchor: 0)));

    [Fact]
    public void A_frame_that_touches_nothing_clears_the_selection_without_a_key_and_keeps_it_with_one()
    {
        Assert.Empty(Marquee.Selection(MarqueeMode.Replace, [1, 2], null, anchor: 1));
        Assert.Equal([1, 2], Sorted(Marquee.Selection(MarqueeMode.Add, [1, 2], null, anchor: 1)));
        Assert.Equal([1, 2], Sorted(Marquee.Selection(MarqueeMode.Extend, [1, 2], null, anchor: 1)));
    }

    [Fact]
    public void With_control_the_frame_adds_to_the_selection()
        => Assert.Equal([0, 4, 5, 9], Sorted(Marquee.Selection(MarqueeMode.Add, [0, 9], (4, 5), anchor: 0)));

    [Fact]
    public void With_shift_the_selection_grows_from_the_anchor_to_the_frame()
    {
        Assert.Equal([2, 3, 4, 5, 6], Sorted(Marquee.Selection(MarqueeMode.Extend, [2], (5, 6), anchor: 2)));
        Assert.Equal([1, 2, 3, 4, 8], Sorted(Marquee.Selection(MarqueeMode.Extend, [8], (1, 2), anchor: 4)));

        // Ohne Anker wie mit Strg.
        Assert.Equal([0, 5, 6], Sorted(Marquee.Selection(MarqueeMode.Extend, [0], (5, 6), anchor: -1)));
    }

    [Fact]
    public void The_list_scrolls_at_its_edges_faster_the_further_out()
    {
        Assert.Equal(0, Marquee.ScrollStep(200, 400));
        Assert.Equal(0, Marquee.ScrollStep(Marquee.ScrollEdge, 400));
        Assert.True(Marquee.ScrollStep(10, 400) < 0);
        Assert.True(Marquee.ScrollStep(390, 400) > 0);
        Assert.True(Marquee.ScrollStep(-60, 400) < Marquee.ScrollStep(10, 400));
        Assert.True(Marquee.ScrollStep(460, 400) > Marquee.ScrollStep(390, 400));

        // Weit draußen nicht schneller als die Grenze.
        Assert.Equal(-Marquee.MaxScrollStep, Marquee.ScrollStep(-5000, 400));
        Assert.Equal(Marquee.MaxScrollStep, Marquee.ScrollStep(5000, 400));
    }

    [Fact]
    public void A_low_list_keeps_a_middle_where_it_stands_still()
    {
        Assert.Equal(0, Marquee.ScrollStep(30, 60));
        Assert.True(Marquee.ScrollStep(5, 60) < 0);
        Assert.Equal(0, Marquee.ScrollStep(30, 0));
        Assert.Equal(0, Marquee.ScrollStep(double.NaN, 400));
    }

    private static List<int> Sorted(HashSet<int> set) => [.. set.Order()];
}
