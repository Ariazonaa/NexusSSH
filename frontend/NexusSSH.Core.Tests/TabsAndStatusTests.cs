using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Angeheftete Reiter, Befehle für viele Reiter und die Übersicht der Tastenkürzel.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TabsAndStatusTests
{
    public TabsAndStatusTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, List<SessionViewModel> Tabs) Open(int count)
    {
        var shell = TestShell.Create(out var backend);
        var profiles = backend.Profiles.GetProfiles();
        var tabs = Enumerable.Range(0, count).Select(index => shell.OpenSession(profiles[index % profiles.Count])).ToList();
        return (shell, tabs);
    }

    [Fact]
    public void A_pinned_tab_moves_to_the_left_and_stays_there()
    {
        var (shell, tabs) = Open(4);

        shell.TogglePin(tabs[2]);
        shell.TogglePin(tabs[3]);
        Assert.Equal([tabs[2], tabs[3], tabs[0], tabs[1]], shell.Sessions);

        // Ein neuer Reiter und ein verschobener landen nie zwischen den angehefteten – und ein angehefteter nie dahinter.
        var added = shell.OpenSession(tabs[0].Profile);
        Assert.True(shell.Sessions.IndexOf(added) >= 2);
        shell.MoveTab(tabs[0], 0);
        Assert.Equal(2, shell.Sessions.IndexOf(tabs[0]));
        shell.MoveTab(tabs[2], 4);
        Assert.Equal(1, shell.Sessions.IndexOf(tabs[2]));

        // Gelöst rückt er an den Anfang der übrigen.
        shell.TogglePin(tabs[3]);
        Assert.Equal((false, 1), (tabs[3].IsPinned, shell.Sessions.IndexOf(tabs[3])));
        Assert.Equal(0, shell.Sessions.IndexOf(tabs[2]));
    }

    [Fact]
    public void Closing_all_others_spares_this_tab_and_the_pinned_ones()
    {
        var (shell, tabs) = Open(4);
        shell.TogglePin(tabs[0]);
        Assert.True(shell.HasOthersToClose(tabs[1]));

        shell.CloseOthers(tabs[2]);

        Assert.Equal([tabs[0], tabs[2]], shell.Sessions);
        Assert.Same(tabs[2], shell.ActiveSession);
        Assert.False(shell.HasOthersToClose(tabs[2]));
        Assert.False(shell.HasOthersToClose(null));

        // Der Befehl zum Schließen gilt auch einem angehefteten – nur das × fehlt ihm.
        shell.CloseSession(tabs[0]);
        Assert.Equal([tabs[2]], shell.Sessions);
    }

    [Fact]
    public void Pinned_tabs_are_remembered_with_the_open_tabs()
    {
        var (shell, tabs) = Open(3);
        shell.RestoreTabs = true;
        shell.TogglePin(tabs[1]);

        shell.RememberOpenSessions();

        Assert.Equal([0], shell.Settings.PinnedTabs);
        Assert.Equal(tabs[1].Profile.Id, shell.Settings.OpenTabs[0]);

        // Was nicht zu den offenen Tabs passt, fällt beim Laden weg.
        var odd = new AppSettings { OpenTabs = ["a", "b"], PinnedTabs = [1, 1, 5, -2, 0] }.Sanitize();
        Assert.Equal([0, 1], odd.PinnedTabs);
    }

    [Fact]
    public void Commands_for_all_tabs_only_offer_what_would_do_something()
    {
        var (shell, tabs) = Open(2);
        ShellCommand Command(string id) => shell.Commands.First(command => command.Id == id);

        Assert.False(shell.HasConnected);
        Assert.False(Command("session.disconnectAll").IsAvailable);
        Assert.True(Command("session.pin").IsAvailable);
        Assert.False(Command("session.unpin").IsAvailable);
        Assert.True(Command("session.closeOthers").IsAvailable);

        shell.ActiveSession = tabs[0];
        Command("session.pin").Run();
        Assert.True(tabs[0].IsPinned);
        Assert.True(Command("session.unpin").IsAvailable);

        tabs[0].Start();
        tabs[1].Start();
        Assert.True(shell.HasConnected);
        shell.DisconnectAll();
        Assert.All(tabs, tab => Assert.True(tab.IsClosed));
        Assert.True(shell.HasClosed);
        shell.ReconnectAll();
        Assert.All(tabs, tab => Assert.False(tab.IsClosed));
    }

    [Fact]
    public void The_shortcut_overview_opens_the_help_there_and_prints_as_plain_html()
    {
        var shell = TestShell.Create(out _);
        var command = shell.Commands.First(candidate => candidate.Id == "view.shortcuts");
        Assert.Equal(new KeyChord("F1", Shift: false), command.Shortcut);

        var before = shell.ShortcutsWanted;
        command.Run();
        Assert.True(shell.IsHelpOpen);
        Assert.Equal(before + 1, shell.ShortcutsWanted);
        command.Run();
        Assert.Equal(before + 2, shell.ShortcutsWanted);

        var page = HelpContent.ShortcutsHtml(shell.Commands, "NexusSSH <b>&</b> shortcuts");
        Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
        Assert.Contains("<h1>NexusSSH &lt;b&gt;&amp;&lt;/b&gt; shortcuts</h1>", page, StringComparison.Ordinal);
        Assert.Contains("<td>Show keyboard shortcuts</td><td class=\"keys\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", page, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HelpContent.Shortcuts(shell.Commands).Count, page.Split("<tr>").Length - 1);
    }
}
