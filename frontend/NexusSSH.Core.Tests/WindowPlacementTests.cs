using NexusSSH.Core.Settings;

namespace NexusSSH.Core.Tests;

/// <summary>Wo das Fenster wieder aufgeht – auch wenn der Bildschirm von damals fehlt.</summary>
public sealed class WindowPlacementTests
{
    private static readonly WindowBounds Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void A_window_on_the_screen_stays_where_it_was()
    {
        var window = new WindowBounds(200, 150, 1000, 700);

        Assert.Equal(window, WindowPlacement.Fit(window, Screen));
    }

    [Fact]
    public void A_window_partly_off_the_screen_stays_as_long_as_its_title_bar_can_be_reached()
    {
        var window = new WindowBounds(1500, 900, 1000, 700);

        Assert.Equal(window, WindowPlacement.Fit(window, Screen));
    }

    [Fact]
    public void A_window_on_a_screen_that_is_gone_comes_back()
    {
        var fitted = WindowPlacement.Fit(new WindowBounds(3000, 200, 1000, 700), Screen);

        Assert.Equal(new WindowBounds(920, 200, 1000, 700), fitted);
    }

    [Fact]
    public void A_title_bar_above_the_screen_comes_back()
    {
        var fitted = WindowPlacement.Fit(new WindowBounds(100, -400, 1000, 700), Screen);

        Assert.Equal(new WindowBounds(100, 0, 1000, 700), fitted);
    }

    [Fact]
    public void A_title_bar_below_the_screen_comes_back()
    {
        var fitted = WindowPlacement.Fit(new WindowBounds(100, 1500, 1000, 700), Screen);

        Assert.Equal(new WindowBounds(100, 340, 1000, 700), fitted);
    }

    [Fact]
    public void A_window_larger_than_a_smaller_screen_shrinks_when_it_must_move()
    {
        var fitted = WindowPlacement.Fit(new WindowBounds(4000, 100, 2500, 1400), Screen);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1040), fitted);
    }

    [Fact]
    public void A_screen_to_the_left_has_negative_positions()
    {
        var left = new WindowBounds(-1920, 0, 1920, 1040);
        var window = new WindowBounds(-1700, 100, 1000, 700);

        Assert.Equal(window, WindowPlacement.Fit(window, left));
    }

    [Fact]
    public void A_task_bar_on_top_moves_the_working_area()
    {
        var fitted = WindowPlacement.Fit(new WindowBounds(100, 0, 1000, 700), new WindowBounds(0, 48, 1920, 1032));

        Assert.Equal(new WindowBounds(100, 48, 1000, 700), fitted);
    }

    [Theory]
    [InlineData(100, 200, 100, 200)]
    [InlineData(-3000, 50, -3000, 50)]
    [InlineData(100, null, null, null)]
    [InlineData(null, 200, null, null)]
    [InlineData(200000, 200, null, null)]
    public void Only_a_whole_position_of_a_sane_size_is_kept(int? x, int? y, int? expectedX, int? expectedY)
    {
        var settings = new AppSettings { WindowX = x, WindowY = y }.Sanitize();

        Assert.Equal((expectedX, expectedY), (settings.WindowX, settings.WindowY));
    }

    [Fact]
    public void A_fresh_installation_leaves_the_place_to_windows()
    {
        var settings = new AppSettings();

        Assert.Null(settings.WindowX);
        Assert.Null(settings.WindowY);
        Assert.False(settings.WindowMaximized);
    }

    [Fact]
    public void The_place_of_the_window_stays_on_this_computer()
        => Assert.DoesNotContain(typeof(Exchange.ExportedSettings).GetProperties(), property => property.Name.StartsWith("Window", StringComparison.Ordinal));
}
