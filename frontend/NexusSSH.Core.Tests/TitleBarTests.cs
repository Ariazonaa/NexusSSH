using NexusSSH.Core.Localization;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Die Knöpfe in der Titelleiste, die die Kopfzeile über dem Terminal ersetzen: Was dort stand, ist weiter zu
/// erreichen – über das Menü „Teilen“, das Menü des Reiters und die Palette.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TitleBarTests
{
    public TitleBarTests() => L.Init(AppLanguage.English);

    [Fact]
    public void Every_entry_of_the_split_menu_is_a_command()
    {
        var shell = TestShell.Create(out _);
        foreach (var id in ShellViewModel.SplitMenuIds)
        {
            Assert.Contains(shell.Commands, command => command.Id == id);
        }

        Assert.Equal(ShellViewModel.SplitMenuIds.Count, ShellViewModel.SplitMenuIds.Distinct().Count());
    }

    [Fact]
    public void The_switches_of_the_split_menu_show_their_state()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();

        Assert.Null(shell.SplitMenuState("terminal.splitRight"));
        Assert.False(shell.SplitMenuState("view.dualFiles"));
        Assert.True(shell.RunCommand("view.dualFiles"));
        Assert.True(shell.SplitMenuState("view.dualFiles"));
        shell.IsDualFiles = false;

        // An alle senden gibt es erst in einer geteilten Ansicht.
        Assert.False(shell.SplitMenuState("terminal.broadcast"));
        Assert.False(shell.Commands.Single(command => command.Id == "terminal.broadcast").IsAvailable);
        Assert.True(shell.RunCommand("terminal.splitRight"));
        Assert.True(shell.RunCommand("terminal.broadcast"));
        Assert.True(shell.SplitMenuState("terminal.broadcast"));
    }

    [Fact]
    public void What_the_header_above_the_terminal_offered_stays_in_the_palette()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();

        // Trennen, danach erneut verbinden – beides als Befehl, wie im Menü des Reiters.
        Assert.True(shell.RunCommand("session.disconnect"));
        Assert.True(session.IsClosed);
        Assert.True(shell.RunCommand("session.reconnect"));
        foreach (var id in (string[])["terminal.find", "view.sftp", "view.snippets", "session.keep"])
        {
            Assert.Contains(shell.Commands, command => command.Id == id);
        }
    }
}
