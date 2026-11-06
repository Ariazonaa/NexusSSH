using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Split View (Spec §5): bis zu sechzehn Sitzungen teilen sich die Terminal-Fläche.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SplitViewTests
{
    private static ShellViewModel WithSession(out SessionViewModel first)
    {
        var shell = TestShell.Create(out var backend);
        first = shell.OpenSession(backend.GetProfiles().Single(profile => profile.Id == "web01"));
        return shell;
    }

    [Fact]
    public void Without_a_session_there_is_nothing_to_show_or_split()
    {
        var shell = TestShell.Create(out _);

        Assert.Same(TerminalLayout.Empty, shell.Layout);
        Assert.Null(shell.Split(sideBySide: true));
        Assert.Empty(shell.Sessions);
    }

    [Fact]
    public void A_single_session_fills_the_terminal_area()
    {
        var shell = WithSession(out var first);

        Assert.Equal([first], shell.Layout.Panes);
        Assert.Same(first, shell.Layout.Active);
        Assert.False(first.IsSplit);
    }

    [Fact]
    public void Splitting_opens_a_second_session_to_the_same_server_next_to_the_first()
    {
        var shell = WithSession(out var first);
        var other = shell.OpenSession(first.Profile with { Id = "x", Name = "other" });
        shell.ActiveSession = first;

        var second = shell.Split(sideBySide: true)!;

        Assert.Same(first.Profile, second.Profile);
        Assert.NotSame(first, second);
        Assert.Equal([first, second, other], shell.Sessions);
        Assert.Same(second, shell.ActiveSession);
        Assert.Equal([first, second], shell.Layout.Panes);
        Assert.True(shell.Layout.RootSideBySide());
        Assert.Same(second, shell.Layout.Active);
        Assert.True(first.IsSplit);
        Assert.True(second.IsSplit);
        Assert.False(other.IsSplit);
    }

    [Fact]
    public void Both_tabs_of_a_split_show_both_terminals_in_the_same_order()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: false)!;

        shell.ActiveSession = first;

        Assert.Equal([first, second], shell.Layout.Panes);
        Assert.False(shell.Layout.RootSideBySide());
        Assert.Same(first, shell.Layout.Active);
    }

    [Fact]
    public void A_tab_outside_the_split_is_shown_alone_and_the_split_survives()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var other = shell.OpenSession(first.Profile);

        Assert.Equal([other], shell.Layout.Panes);

        shell.ActiveSession = second;
        Assert.Equal([first, second], shell.Layout.Panes);
    }

    [Fact]
    public void Removing_the_split_keeps_both_sessions_as_tabs()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;

        shell.Unsplit();

        Assert.Equal([first, second], shell.Sessions);
        Assert.Equal([second], shell.Layout.Panes);
        Assert.False(first.IsSplit);
        Assert.False(second.IsSplit);
    }

    [Fact]
    public void Closing_one_half_leaves_the_other_alone()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;

        shell.CloseSession(second);

        Assert.Equal([first], shell.Sessions);
        Assert.Same(first, shell.ActiveSession);
        Assert.Equal([first], shell.Layout.Panes);
        Assert.False(first.IsSplit);
    }

    [Fact]
    public void Splitting_again_adds_a_third_terminal_right_after_the_active_one()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        shell.ActiveSession = first;

        var third = shell.Split(sideBySide: false)!;

        Assert.Equal([first, third, second], shell.Layout.Panes);
        Assert.Equal([first, third, second], shell.Sessions);
        Assert.Same(third, shell.Layout.Active);
        Assert.All(shell.Sessions, session => Assert.True(session.IsSplit));
    }

    // Der Baum als Text: Nummern der Tabs, „|“ nebeneinander, „/“ untereinander.
    private static string Shape(ShellViewModel shell, SplitNode<SessionViewModel>? node = null)
    {
        node ??= shell.Layout.Tree;
        return node switch
        {
            SplitLeaf<SessionViewModel> leaf => shell.Sessions.IndexOf(leaf.Item).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SplitBranch<SessionViewModel> branch => "[" + string.Join(branch.SideBySide ? "|" : "/", branch.Children.Select(child => Shape(shell, child))) + "]",
            _ => string.Empty,
        };
    }

    [Fact]
    public void Each_split_goes_the_way_asked_even_inside_a_group()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: false)!;

        // Früher folgte jede weitere Teilung der Richtung der ersten.
        Assert.Equal("[0|[1/2]]", Shape(shell));
        Assert.Equal([first, second, third], shell.Layout.Panes);
        Assert.Same(third, shell.Layout.Active);
        Assert.False(shell.Layout.Fixed);
    }

    [Fact]
    public void Up_to_sixteen_terminals_share_the_area()
    {
        var shell = WithSession(out _);
        for (var count = 1; count < SplitTree.MaxPanes; count++)
        {
            Assert.True(shell.CanSplit);
            Assert.NotNull(shell.Split(sideBySide: count % 2 == 0));
        }

        Assert.Equal(SplitTree.MaxPanes, shell.Layout.Panes.Count);
        Assert.False(shell.CanSplit);
        Assert.Null(shell.Split(sideBySide: true));
        Assert.Equal(16, shell.Sessions.Count);
        Assert.False(shell.RunCommand("terminal.splitRight"));

        // Auch ein vorhandener Tab passt nicht mehr dazu.
        var other = shell.OpenSession(shell.Sessions[0].Profile);
        shell.ActiveSession = shell.Sessions[0];
        shell.SplitWith(other, sideBySide: true);
        Assert.False(other.IsSplit);
        Assert.Equal(16, shell.Layout.Panes.Count);
    }

    [Fact]
    public void Closing_a_pane_keeps_the_focus_inside_the_split()
    {
        var shell = WithSession(out var first);
        var other = shell.OpenSession(first.Profile with { Id = "x", Name = "other" });
        shell.ActiveSession = first;
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: true)!;
        Assert.Equal([first, second, third, other], shell.Sessions);

        // Der letzte Bereich geht: Der Fokus bleibt in der Teilung, nicht beim Nachbarn-Tab.
        shell.CloseSession(third);
        Assert.Same(second, shell.ActiveSession);
        Assert.Equal([first, second], shell.Layout.Panes);

        // Aus der Mitte: der nächste Bereich.
        var again = shell.Split(sideBySide: true)!;
        shell.ActiveSession = second;
        shell.CloseSession(second);
        Assert.Same(again, shell.ActiveSession);
        Assert.Equal([first, again], shell.Layout.Panes);
        Assert.False(other.IsSplit);
    }

    [Fact]
    public void Zoom_shows_one_pane_and_splitting_or_moving_the_focus_ends_it()
    {
        var shell = WithSession(out var first);
        Assert.False(shell.CanZoom);
        shell.ToggleZoom();
        Assert.Null(shell.Layout.Zoomed);

        var second = shell.Split(sideBySide: true)!;
        Assert.True(shell.CanZoom);
        Assert.True(shell.RunCommand("terminal.zoomPane"));
        Assert.Same(second, shell.Layout.Zoomed);
        Assert.Equal([first, second], shell.Layout.Panes);

        // Noch einmal: wieder alle.
        shell.ToggleZoom();
        Assert.Null(shell.Layout.Zoomed);

        // Teilen beendet es.
        shell.ToggleZoom();
        var third = shell.Split(sideBySide: false)!;
        Assert.Null(shell.Layout.Zoomed);

        // Den Fokus mit den Tasten zu bewegen auch – und er geht trotzdem weiter.
        shell.ToggleZoom();
        Assert.Same(third, shell.Layout.Zoomed);
        shell.FocusPane(PaneDirection.Up);
        Assert.Null(shell.Layout.Zoomed);
        Assert.Same(second, shell.ActiveSession);

        // Ein anderer Tab der Teilung wählt den Bereich, den er zeigt.
        shell.ToggleZoom();
        shell.ActiveSession = first;
        Assert.Null(shell.Layout.Zoomed);
        Assert.Same(first, shell.Layout.Active);

        // Der vergrößerte Bereich geht: keine Vergrößerung mehr.
        shell.ToggleZoom();
        shell.CloseSession(first);
        Assert.Null(shell.Layout.Zoomed);
    }

    [Fact]
    public void The_keys_move_to_the_neighbour_in_their_direction()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: false)!;
        Assert.Equal("[0|[1/2]]", Shape(shell));

        shell.FocusPane(PaneDirection.Left);
        Assert.Same(first, shell.ActiveSession);
        shell.FocusPane(PaneDirection.Left);
        Assert.Same(first, shell.ActiveSession);

        Assert.True(shell.RunCommand("terminal.focusRight"));
        Assert.Same(second, shell.ActiveSession);
        Assert.True(shell.RunCommand("terminal.focusDown"));
        Assert.Same(third, shell.ActiveSession);
        shell.FocusPane(PaneDirection.Down);
        Assert.Same(third, shell.ActiveSession);
        Assert.True(shell.RunCommand("terminal.focusUp"));
        Assert.Same(second, shell.ActiveSession);
        Assert.True(shell.RunCommand("terminal.focusLeft"));
        Assert.Same(first, shell.ActiveSession);

        // Ohne Teilung gibt es nichts anzusteuern.
        shell.Unsplit();
        shell.Unsplit();
        Assert.False(shell.RunCommand("terminal.focusRight"));
    }

    [Fact]
    public void A_move_from_the_page_rearranges_and_a_tmux_window_refuses_it()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: true)!;
        Assert.Equal("[0|1|2]", Shape(shell));

        Assert.True(shell.MovePane(third, first, PaneSide.Top));
        Assert.Equal("[[2/0]|1]", Shape(shell));
        Assert.Same(third, shell.Layout.Active);
        Assert.Equal([first, second, third], shell.Sessions);

        // Was nicht geht, ändert nichts.
        Assert.False(shell.MovePane(third, third, PaneSide.Left));
        var elsewhere = shell.OpenSession(first.Profile);
        shell.ActiveSession = third;
        Assert.False(shell.MovePane(third, elsewhere, PaneSide.Left));
        Assert.Equal("[[2/0]|1]", Shape(shell));

        // Gleich verteilt, dann weiter umgestellt.
        Assert.True(shell.SetShares(first, [], [0.3, 0.7]));
        Assert.True(shell.RunCommand("terminal.equalize"));
        Assert.Equal([0.5, 0.5], Assert.IsType<SplitBranch<SessionViewModel>>(shell.Layout.Tree).Shares);
        Assert.True(shell.MovePane(second, third, PaneSide.Bottom));
        Assert.Equal("[2/1/0]", Shape(shell));
    }

    [Fact]
    public void A_divider_is_reached_from_the_keyboard_through_a_command_only_while_the_terminals_are_split()
    {
        var shell = WithSession(out _);
        var requests = new List<UiRequest>();
        shell.UiRequested += requests.Add;

        // Ein Terminal allein hat keine Linie; Tab gehört der Shell, also führt nur der Befehl zu ihr.
        Assert.False(shell.RunCommand("terminal.focusDivider"));
        shell.Split(sideBySide: true);
        Assert.True(shell.RunCommand("terminal.focusDivider"));
        Assert.Equal([UiRequest.FocusDivider], requests);
        Assert.Null(shell.Commands.Single(command => command.Id == "terminal.focusDivider").Shortcut);
    }

    [Fact]
    public void Shares_for_a_split_that_is_no_longer_shown_change_nothing()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: false)!;

        Assert.True(shell.SetShares(first, [], [0.3, 0.7]));
        Assert.True(shell.SetShares(second, [1], [0.6, 0.4]));
        var root = Assert.IsType<SplitBranch<SessionViewModel>>(shell.Layout.Tree);
        Assert.Equal([0.3, 0.7], root.Shares);
        Assert.Equal([0.6, 0.4], Assert.IsType<SplitBranch<SessionViewModel>>(root.Children[1]).Shares);

        // Was das Gebilde nicht kennt, ändert nichts.
        Assert.False(shell.SetShares(first, [], [0.5, 0.5, 0.0]));
        Assert.False(shell.SetShares(first, [], [double.NaN, 0.5]));
        Assert.False(shell.SetShares(first, [0], [0.5, 0.5]));
        Assert.False(shell.SetShares(first, [7], [0.5, 0.5]));
        Assert.Equal([0.3, 0.7], Assert.IsType<SplitBranch<SessionViewModel>>(shell.Layout.Tree).Shares);

        // Eine Teilung, die nicht (mehr) zu sehen ist: ein anderer Tab, ein Bereich ohne Teilung.
        var alone = shell.OpenSession(first.Profile);
        Assert.False(shell.SetShares(first, [], [0.5, 0.5]));
        shell.ActiveSession = first;
        shell.Unsplit();
        shell.Unsplit();
        Assert.False(shell.SetShares(second, [], [0.5, 0.5]));
        Assert.False(shell.SetShares(alone, [], [0.5, 0.5]));

        // Und eine Teilung, die es nicht mehr gibt.
        shell.ActiveSession = second;
        shell.Unsplit();
        Assert.False(third.IsSplit);
        Assert.False(shell.SetShares(third, [], [0.5, 0.5]));
        Assert.IsType<SplitLeaf<SessionViewModel>>(shell.Layout.Tree);
    }

    [Fact]
    public void Leaving_a_group_of_three_keeps_the_other_two_together()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: true)!;

        shell.Unsplit();

        Assert.Equal([third], shell.Layout.Panes);
        Assert.False(third.IsSplit);
        shell.ActiveSession = first;
        Assert.Equal([first, second], shell.Layout.Panes);

        // Schließt eines der beiden, ist das letzte wieder allein.
        shell.CloseSession(second);
        Assert.Equal([first], shell.Layout.Panes);
        Assert.False(first.IsSplit);
    }

    [Fact]
    public void A_tab_from_another_split_moves_over()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.OpenSession(first.Profile);
        var fourth = shell.Split(sideBySide: true)!;

        shell.ActiveSession = first;
        shell.SplitWith(fourth, sideBySide: true);

        Assert.Equal([first, fourth, second], shell.Layout.Panes);
        Assert.False(third.IsSplit, "allein zurückgeblieben ist keine Teilung mehr");
        shell.ActiveSession = third;
        Assert.Equal([third], shell.Layout.Panes);
    }

    [Fact]
    public void The_active_tab_can_be_moved_left_and_right()
    {
        var shell = WithSession(out var first);
        var second = shell.OpenSession(first.Profile);
        var third = shell.OpenSession(first.Profile);

        Assert.False(shell.RunCommand("session.moveRight"), "der letzte Tab kann nicht weiter nach rechts");
        Assert.True(shell.RunCommand("session.moveLeft"));
        Assert.Equal([first, third, second], shell.Sessions);
        Assert.Same(third, shell.ActiveSession);

        Assert.True(shell.MoveActiveTab(-1));
        Assert.Equal([third, first, second], shell.Sessions);
        Assert.False(shell.MoveActiveTab(-1));

        Assert.True(shell.MoveActiveTab(+1));
        Assert.Equal([first, third, second], shell.Sessions);
    }

    [Fact]
    public void A_split_uses_the_standing_connection_of_its_neighbour()
    {
        var profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);
        var backend = new ScriptedBackend(profile);
        var shell = TestShell.Create(backend);

        var first = shell.OpenSession(profile);
        Assert.Null(backend.Last.Shared);
        first.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        var standing = backend.Last;

        shell.Split(sideBySide: true);
        Assert.Same(standing, backend.Last.Shared);

        // Auch ein weiterer Tab zum selben Server fährt mit – ein anderer Server nicht.
        shell.OpenSession(profile);
        Assert.Same(standing, backend.Last.Shared);
        shell.OpenSession(profile with { Id = "p2", Name = "other" });
        Assert.Null(backend.Last.Shared);
    }

    [Fact]
    public void A_session_waits_for_another_one_that_is_signing_in_and_then_rides_along()
    {
        var profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);
        var backend = new ScriptedBackend(profile);
        var shell = TestShell.Create(backend);

        // Zwei Tabs zum selben Server starten zugleich (z. B. nach dem Wiederherstellen einer Teilung).
        var first = shell.OpenSession(profile);
        var second = shell.OpenSession(profile);
        var other = shell.OpenSession(profile with { Id = "p2", Name = "db01" });
        first.Start();
        var signingIn = backend.Connections[0];
        second.Start();
        other.Start();

        Assert.True(signingIn.Started);
        Assert.False(backend.Connections[1].Started, "die zweite Sitzung meldet sich nicht daneben noch einmal an");
        Assert.True(backend.Connections[2].Started, "ein anderer Server wartet auf niemanden");

        signingIn.Set(new ConnectionStatus(ConnectionState.Authenticating));
        Assert.Equal(3, backend.Connections.Count);

        signingIn.Set(new ConnectionStatus(ConnectionState.Connected));

        // Jetzt verbindet die zweite – über die Verbindung der ersten.
        Assert.Equal(4, backend.Connections.Count);
        Assert.Same(signingIn, backend.Last.Shared);
        Assert.True(backend.Last.Started);
        Assert.True(backend.Connections[1].Disposed, "die vorbereitete Verbindung ohne Mitfahrt ist verworfen");
    }

    [Fact]
    public void A_waiting_session_connects_on_its_own_when_the_other_one_fails_or_closes()
    {
        var profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);
        var backend = new ScriptedBackend(profile);
        var shell = TestShell.Create(backend);
        var first = shell.OpenSession(profile);
        var second = shell.OpenSession(profile);
        var third = shell.OpenSession(profile);
        first.Start();
        second.Start();
        third.Start();
        Assert.Equal([true, false, false], backend.Connections.Select(connection => connection.Started));

        // Die erste scheitert: Die zweite versucht es selbst – und die dritte wartet nun auf die zweite.
        backend.Connections[0].Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.AuthFailed));
        Assert.Equal(4, backend.Connections.Count);
        Assert.Null(backend.Last.Shared);
        Assert.True(backend.Last.Started);

        // Wird die zweite geschlossen, während die dritte wartet, hängt die dritte nicht fest.
        shell.CloseSession(second);
        Assert.Equal(5, backend.Connections.Count);
        Assert.True(backend.Last.Started);
        Assert.Null(backend.Last.Shared);
    }

    [Fact]
    public void A_waiting_session_that_is_closed_never_connects()
    {
        var profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);
        var backend = new ScriptedBackend(profile);
        var shell = TestShell.Create(backend);
        var first = shell.OpenSession(profile);
        var second = shell.OpenSession(profile);
        first.Start();
        second.Start();

        shell.CloseSession(second);
        backend.Connections[0].Set(new ConnectionStatus(ConnectionState.Connected));

        Assert.Equal(2, backend.Connections.Count);
        Assert.False(backend.Connections[1].Started);
    }

    [Fact]
    public void Nothing_is_shared_with_a_connection_that_is_not_established()
    {
        var profile = new ServerProfile("p1", "web01", "10.0.1.10", 22, "ubuntu", string.Empty, []);
        var backend = new ScriptedBackend(profile);
        var shell = TestShell.Create(backend);

        var first = shell.OpenSession(profile);
        first.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Authenticating));

        shell.Split(sideBySide: true);
        Assert.Null(backend.Last.Shared);

        // Nach dem Ende der ersten Verbindung genauso.
        backend.Connections[0].Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.Remote));
        shell.OpenSession(profile);
        Assert.Null(backend.Last.Shared);
    }

    [Fact]
    public void An_open_tab_can_be_put_next_to_the_active_one()
    {
        var shell = WithSession(out var first);
        var other = shell.OpenSession(first.Profile with { Id = "x", Name = "other" });
        shell.ActiveSession = first;

        shell.SplitWith(other, sideBySide: true);

        Assert.Equal([first, other], shell.Layout.Panes);
        Assert.Same(first, shell.ActiveSession);
        Assert.Equal(2, shell.Sessions.Count);

        // Mit sich selbst lässt sich nichts teilen.
        shell.Unsplit();
        shell.SplitWith(first, sideBySide: true);
        Assert.Equal([first], shell.Layout.Panes);
    }

    [Fact]
    public void Tabs_cycle_in_both_directions()
    {
        var shell = WithSession(out var first);
        var second = shell.OpenSession(first.Profile);
        var third = shell.OpenSession(first.Profile);

        shell.ActivateRelative(+1);
        Assert.Same(first, shell.ActiveSession);
        shell.ActivateRelative(-1);
        Assert.Same(third, shell.ActiveSession);
        shell.ActivateRelative(-1);
        Assert.Same(second, shell.ActiveSession);
    }

    [Fact]
    public void A_move_for_a_split_that_is_not_shown_changes_nothing_and_a_full_split_can_still_be_rearranged()
    {
        var shell = WithSession(out var first);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: true)!;
        var before = Shape(shell);

        // Ein anderer Tab ist aktiv: Eine späte Meldung der Seite stellt in der verborgenen Teilung nichts um.
        var alone = shell.OpenSession(first.Profile);
        Assert.False(shell.MovePane(third, first, PaneSide.Top));
        shell.ActiveSession = first;
        Assert.Equal(before, Shape(shell));
        Assert.Same(alone, shell.Sessions[^1]);

        // Mit sechzehn Bereichen geht Umsetzen weiter, Teilen nicht.
        while (shell.CanSplit)
        {
            shell.Split(sideBySide: true);
        }

        Assert.Equal(16, shell.Layout.Panes.Count);
        shell.ActiveSession = first;
        var last = shell.Layout.Panes[^1];
        shell.SplitWith(last, sideBySide: false);
        Assert.Equal(16, shell.Layout.Panes.Count);
        Assert.Contains("/", Shape(shell));
    }
}
