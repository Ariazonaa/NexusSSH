using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>„Nur das Terminal“: alles andere weicht, und der Modus endet, sobald etwas anderes an der Reihe ist.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TerminalOnlyTests
{
    public TerminalOnlyTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, SessionViewModel Session) Open()
    {
        var shell = TestShell.Create(out var backend);
        var session = shell.OpenSession(backend.GetProfiles()[0]);
        session.Start();
        shell.IsSftpVisible = shell.IsSnippetsVisible = shell.IsBottomPanelVisible = true;
        shell.IsDualFiles = false;
        return (shell, session);
    }

    [Fact]
    public void Everything_but_the_terminal_gives_way_and_comes_back_as_it_was()
    {
        var (shell, _) = Open();
        List<string> changed = [];
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        Assert.Equal((true, true, true), (shell.ShowsFiles, shell.ShowsSnippets, shell.ShowsBottom));

        Assert.True(shell.RunCommand("view.terminalOnly"));
        Assert.True(shell.IsTerminalOnly);
        Assert.Equal((false, false, false), (shell.ShowsFiles, shell.ShowsSnippets, shell.ShowsBottom));
        Assert.Contains(nameof(ShellViewModel.IsTerminalOnly), changed);

        // Die Schalter der Bereiche bleiben, wie sie sind – und in den Einstellungen steht nichts von dem Modus.
        Assert.Equal((true, true, true), (shell.IsSftpVisible, shell.IsSnippetsVisible, shell.IsBottomPanelVisible));

        // Von Bereich zu Bereich führt jetzt kein Weg: Es gibt nur das Terminal.
        Assert.Equal(ShellPane.Terminal, shell.PaneAfter(ShellPane.Terminal, 1));

        // Ohne Inspector misst niemand.
        var monitor = Assert.IsType<MockServerMonitor>(shell.ActiveSession!.Inspector.Monitor);
        Assert.False(monitor.IsActive);

        Assert.True(shell.RunCommand("view.terminalOnly"));
        Assert.False(shell.IsTerminalOnly);
        Assert.Equal((true, true, true), (shell.ShowsFiles, shell.ShowsSnippets, shell.ShowsBottom));
        Assert.True(monitor.IsActive);
    }

    [Fact]
    public void The_mode_ends_as_soon_as_something_else_than_the_terminal_is_shown()
    {
        var (shell, session) = Open();

        // Die Startseite: kein Terminal, also auch kein „nur“.
        shell.IsTerminalOnly = true;
        shell.ShowHome();
        Assert.False(shell.IsTerminalOnly);
        Assert.False(shell.RunCommand("view.terminalOnly"));
        Assert.False(shell.IsTerminalOnly);

        shell.ActiveSession = session;
        foreach (var leave in (Action[])[() => shell.IsSettingsOpen = true, () => shell.IsHelpOpen = true, () => shell.IsDualFiles = true, shell.ShowTransfers, () => shell.RunCommand("server.logs"), () => shell.Detach(session)])
        {
            shell.IsTerminalOnly = true;
            Assert.True(shell.IsTerminalOnly);
            leave();
            Assert.False(shell.IsTerminalOnly);

            shell.IsSettingsOpen = shell.IsHelpOpen = shell.IsDualFiles = false;
            shell.CloseLogs();
            shell.Reattach(session);
            shell.ActiveSession = session;
        }

        // Ein anderer Reiter ist auch ein Terminal: Dort gilt der Modus weiter.
        var other = shell.OpenSession(shell.Sessions[0].Profile);
        other.Start();
        shell.ActiveSession = session;
        shell.IsTerminalOnly = true;
        shell.ActiveSession = other;
        Assert.True(shell.IsTerminalOnly);

        // Schließt die letzte Sitzung, ist die Titelleiste wieder da.
        shell.CloseSession(other);
        shell.CloseSession(session);
        Assert.False(shell.IsTerminalOnly);
    }
}
