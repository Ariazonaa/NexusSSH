using NexusSSH.Core.Localization;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Mit der Tastatur von Bereich zu Bereich – der Weg aus dem Terminal heraus, in dem Tab der Shell gehört.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class PaneNavigationTests
{
    [Fact]
    public void The_panes_follow_each_other_in_a_circle_in_both_directions()
    {
        var shell = TestShell.Create(out _);
        shell.OpenSession(shell.Profiles[0]);
        (shell.IsSftpVisible, shell.IsSnippetsVisible, shell.IsBottomPanelVisible) = (true, true, true);

        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(ShellPane.Files, +1));
        Assert.Equal(ShellPane.Snippets, shell.PaneAfter(ShellPane.Terminal, +1));
        Assert.Equal(ShellPane.Bottom, shell.PaneAfter(ShellPane.Snippets, +1));
        Assert.Equal(ShellPane.Files, shell.PaneAfter(ShellPane.Bottom, +1));

        Assert.Equal(ShellPane.Bottom, shell.PaneAfter(ShellPane.Files, -1));
        Assert.Equal(ShellPane.Files, shell.PaneAfter(ShellPane.Terminal, -1));
    }

    [Fact]
    public void Hidden_panes_are_skipped_and_the_terminal_is_always_there()
    {
        var shell = TestShell.Create(out _);
        shell.OpenSession(shell.Profiles[0]);
        (shell.IsSftpVisible, shell.IsSnippetsVisible, shell.IsBottomPanelVisible) = (true, false, true);

        Assert.Equal(ShellPane.Bottom, shell.PaneAfter(ShellPane.Terminal, +1));
        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(ShellPane.Bottom, -1));

        (shell.IsSftpVisible, shell.IsBottomPanelVisible) = (false, false);
        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(ShellPane.Terminal, +1));
        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(ShellPane.Terminal, -1));

        // Der Fokus lag in einem Bereich, der inzwischen ausgeblendet ist: wie von außen.
        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(ShellPane.Snippets, +1));
    }

    [Fact]
    public void From_outside_the_panes_forward_starts_at_the_first_and_backward_at_the_last()
    {
        var shell = TestShell.Create(out _);
        shell.OpenSession(shell.Profiles[0]);
        (shell.IsSftpVisible, shell.IsSnippetsVisible, shell.IsBottomPanelVisible) = (true, true, true);

        Assert.Equal(ShellPane.Files, shell.PaneAfter(null, +1));
        Assert.Equal(ShellPane.Bottom, shell.PaneAfter(null, -1));

        shell.IsSftpVisible = false;
        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(null, +1));
    }

    [Fact]
    public void On_the_start_page_file_browser_and_inspector_step_aside_for_the_servers()
    {
        var shell = TestShell.Create(out _);
        (shell.IsSftpVisible, shell.IsSnippetsVisible, shell.IsBottomPanelVisible) = (true, true, true);

        // Noch keine Sitzung: Dateibrowser und Snippets hätten nichts zu zeigen.
        Assert.False(shell.ShowsFiles);
        Assert.False(shell.ShowsSnippets);
        Assert.Equal(ShellPane.Bottom, shell.PaneAfter(ShellPane.Terminal, +1));
        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(null, +1));

        shell.OpenSession(shell.Profiles[0]);
        Assert.True(shell.ShowsFiles);
        Assert.True(shell.ShowsSnippets);

        // Zurück auf die Startseite: wieder ohne die beiden – eingeschaltet bleiben sie.
        shell.ShowHome();
        Assert.False(shell.ShowsFiles);
        Assert.True(shell.IsSftpVisible);
        Assert.True(shell.Settings.ShowSftp);
    }

    [Fact]
    public void The_commands_ask_the_window_to_move_the_focus_and_carry_shortcuts_the_shell_does_not_need()
    {
        L.Init(AppLanguage.English);
        var shell = TestShell.Create(out _);
        var requests = new List<UiRequest>();
        shell.UiRequested += requests.Add;

        Assert.True(shell.RunCommand("view.nextPane"));
        Assert.True(shell.RunCommand("view.previousPane"));
        Assert.Equal([UiRequest.FocusNextPane, UiRequest.FocusPreviousPane], requests);

        // Mit Strg: F6 allein gehört im Terminal dem Programm auf dem Server.
        var next = shell.Commands.Single(command => command.Id == "view.nextPane");
        var previous = shell.Commands.Single(command => command.Id == "view.previousPane");
        Assert.Equal(("Ctrl+F6", "Ctrl+Shift+F6"), (next.Shortcut!.Display, previous.Shortcut!.Display));
        Assert.Contains(shell.SearchPalette(">pane"), item => item.Title == "Focus next pane" && item.Shortcut == "Ctrl+F6");
    }
}
