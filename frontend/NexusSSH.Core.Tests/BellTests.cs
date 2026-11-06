using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public class BellTests
{
    private static (ShellViewModel Shell, SessionViewModel First, SessionViewModel Second) TwoSessions()
    {
        var shell = TestShell.Create(out var backend);
        var first = shell.OpenSession(backend.Profiles.GetProfiles()[0]);
        var second = shell.OpenSession(backend.Profiles.GetProfiles()[1]);
        return (shell, first, second);
    }

    [Fact]
    public void A_bell_in_a_tab_that_is_not_in_front_marks_it_until_it_is_looked_at()
    {
        var (shell, first, second) = TwoSessions();
        Assert.Same(second, shell.ActiveSession);

        shell.OnTerminalBell(second);
        Assert.False(second.HasBell);

        shell.OnTerminalBell(first);
        Assert.True(first.HasBell);

        shell.ActiveSession = first;
        Assert.False(first.HasBell);
    }

    [Fact]
    public void Sound_and_taskbar_are_off_until_chosen_and_come_at_most_every_few_seconds()
    {
        var (shell, first, _) = TwoSessions();
        var rang = new List<(bool Sound, bool Flash)>();
        shell.BellRang += (sound, flash) => rang.Add((sound, flash));

        shell.OnTerminalBell(first);
        Assert.Empty(rang);

        shell.BellSound = true;
        shell.BellTaskbar = true;
        for (var ring = 0; ring < 50; ring++)
        {
            shell.OnTerminalBell(first);
        }

        // Der erste Ton fiel in die Pause nach der Glocke davor – also keiner, oder genau einer.
        Assert.True(rang.Count <= 1);
        Assert.All(rang, entry => Assert.Equal((true, true), entry));
        Assert.True(shell.Settings is { BellSound: true, BellTaskbar: true });
    }

    [Fact]
    public void Switching_the_tab_mark_off_clears_the_marks_that_are_there()
    {
        var (shell, first, _) = TwoSessions();
        shell.OnTerminalBell(first);
        Assert.True(first.HasBell);

        shell.BellTab = false;

        Assert.False(first.HasBell);
        shell.OnTerminalBell(first);
        Assert.False(first.HasBell);
    }
}
