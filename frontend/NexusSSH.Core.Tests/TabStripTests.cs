using System.Collections.Specialized;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Tabs mit der Maus umsortieren: Die Reiterleiste nimmt einen Reiter aus ihrer Liste und setzt ihn woanders ein;
/// für die Sitzungen ist das ein Verschieben, nie ein Schließen.
/// </summary>
public sealed class TabStripTests
{
    private static ShellViewModel WithThree(out SessionViewModel first, out SessionViewModel second, out SessionViewModel third)
    {
        var shell = TestShell.Create(out var backend);
        var profile = backend.GetProfiles().Single(profile => profile.Id == "web01");
        first = shell.OpenSession(profile);
        second = shell.OpenSession(profile);
        third = shell.OpenSession(profile);
        return shell;
    }

    // Was eine Reiterleiste beim Ziehen tut: herausnehmen, an anderer Stelle einsetzen.
    private static void Drag(TabStrip strip, SessionViewModel tab, int to)
    {
        strip.Tabs.Remove(tab);
        Assert.True(strip.IsReordering);
        strip.Tabs.Insert(to, tab);
        Assert.False(strip.IsReordering);
    }

    [Fact]
    public void The_strip_follows_the_sessions()
    {
        var shell = WithThree(out var first, out var second, out var third);
        var strip = new TabStrip(shell);
        Assert.Equal([first, second, third], strip.Tabs);

        var fourth = shell.OpenSession(first.Profile);
        Assert.Equal([first, second, third, fourth], strip.Tabs);

        // Eine Teilung setzt ihren Tab neben den aktiven, nicht ans Ende.
        shell.ActiveSession = first;
        var beside = shell.Split(sideBySide: true)!;
        Assert.Equal([first, beside, second, third, fourth], strip.Tabs);

        shell.CloseSession(second);
        Assert.Equal([first, beside, third, fourth], strip.Tabs);

        shell.ActiveSession = fourth;
        Assert.True(shell.MoveActiveTab(-1));
        Assert.Equal([first, beside, fourth, third], strip.Tabs);
        Assert.Equal(shell.Sessions, strip.Tabs);
        Assert.False(strip.IsReordering);
    }

    [Fact]
    public void Dragging_a_tab_moves_its_session_without_ever_removing_it()
    {
        var shell = WithThree(out var first, out var second, out var third);
        var strip = new TabStrip(shell);
        var changes = new List<NotifyCollectionChangedAction>();
        shell.Sessions.CollectionChanged += (_, e) => changes.Add(e.Action);
        shell.ActiveSession = second;

        Drag(strip, first, to: 2);

        Assert.Equal([second, third, first], shell.Sessions);
        Assert.Equal(shell.Sessions, strip.Tabs);

        // Für alle, die den Sitzungen zuhören (das Terminal!), war das ein Verschieben – nie fehlte die Sitzung.
        Assert.Equal([NotifyCollectionChangedAction.Move], changes);

        // Wie im Browser: Der gezogene Tab ist danach der aktive.
        Assert.Same(first, shell.ActiveSession);

        Drag(strip, third, to: 0);
        Assert.Equal([third, second, first], shell.Sessions);
        Assert.Same(third, shell.ActiveSession);
    }

    [Fact]
    public void A_tab_put_back_where_it_was_changes_nothing_but_becomes_active()
    {
        var shell = WithThree(out var first, out var second, out var third);
        var strip = new TabStrip(shell);
        var changes = 0;
        shell.Sessions.CollectionChanged += (_, _) => changes++;

        Drag(strip, second, to: 1);

        Assert.Equal([first, second, third], shell.Sessions);
        Assert.Equal(shell.Sessions, strip.Tabs);
        Assert.Equal(0, changes);
        Assert.Same(second, shell.ActiveSession);
    }

    [Fact]
    public void What_the_strip_does_on_its_own_is_undone()
    {
        var shell = WithThree(out var first, out var second, out var third);
        var strip = new TabStrip(shell);

        // Tabs öffnet und schließt nur die Shell – eine Leiste, die mehr tut als umsetzen, wird zurückgestellt.
        strip.Tabs.Remove(second);
        strip.Tabs.Add(first);
        Assert.Equal([first, second, third], strip.Tabs);
        Assert.False(strip.IsReordering);

        strip.Tabs.Clear();
        Assert.Equal([first, second, third], strip.Tabs);
        Assert.Equal([first, second, third], shell.Sessions);
    }

    [Fact]
    public void A_session_that_closes_while_its_tab_is_lifted_stays_closed()
    {
        var shell = WithThree(out var first, out var second, out var third);
        var strip = new TabStrip(shell);

        strip.Tabs.Remove(second);
        shell.CloseSession(second);

        Assert.False(strip.IsReordering);
        Assert.Equal([first, third], strip.Tabs);

        // Die Leiste setzt ihn trotzdem ein: Er verschwindet wieder.
        strip.Tabs.Insert(0, second);
        Assert.Equal([first, third], strip.Tabs);
        Assert.Equal([first, third], shell.Sessions);
    }
}
