using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Aktivität und Stille am Reiter: ein Punkt für neue Ausgabe im Hintergrund, eine Meldung, wenn es still wird.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ActivityTests
{
    public ActivityTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, SessionViewModel First, SessionViewModel Second) TwoTabs()
    {
        var shell = TestShell.Create(out _);
        var first = shell.OpenSession(shell.Profiles[0]);
        first.Start();
        var second = shell.OpenSession(shell.Profiles[1]);
        second.Start();
        Assert.Same(second, shell.ActiveSession);
        return (shell, first, second);
    }

    // Etwas tippen: Die Shell der Beispieldaten antwortet – das ist Ausgabe vom „Server“.
    private static void Output(SessionViewModel session)
        => session.Connection.Terminal.Write(Encoding.UTF8.GetBytes("echo hi\r"));

    [Fact]
    public void Output_in_a_tab_out_of_view_puts_a_dot_on_it_until_it_is_looked_at()
    {
        var (shell, first, second) = TwoTabs();

        Output(second);
        Assert.False(second.ShowsActivity);

        Output(first);
        Assert.True(first.ShowsActivity);

        shell.ActiveSession = first;
        Assert.False(first.ShowsActivity);
        Assert.False(first.HasActivity);
    }

    [Fact]
    public void The_bell_goes_before_the_dot()
    {
        var (shell, first, _) = TwoTabs();

        Output(first);
        shell.OnTerminalBell(first);

        Assert.True(first.HasBell);
        Assert.True(first.HasActivity);
        Assert.False(first.ShowsActivity);
    }

    [Fact]
    public void A_pane_of_the_split_in_front_counts_as_seen()
    {
        var (shell, first, second) = TwoTabs();
        shell.SplitWith(first, sideBySide: true);
        shell.ActiveSession = second;

        Output(first);

        Assert.False(first.ShowsActivity);
    }

    [Fact]
    public void Without_the_setting_there_is_no_dot_and_switching_it_off_clears_it()
    {
        var (shell, first, _) = TwoTabs();
        Output(first);
        Assert.True(first.ShowsActivity);

        shell.ActivityTab = false;
        Assert.False(first.ShowsActivity);

        Output(first);
        Assert.False(first.ShowsActivity);
    }

    [Fact]
    public void Silence_is_reported_once_after_output_and_again_only_after_new_output()
    {
        var (shell, first, _) = TwoTabs();
        var now = DateTimeOffset.UnixEpoch;
        shell.Now = () => now;
        var notices = new List<(string Title, string Text)>();
        shell.AttentionRequested += (title, text) => notices.Add((title, text));

        shell.ToggleSilenceWatch(first);
        Assert.True(first.WatchesSilence);

        // Still von Anfang an: Ohne Ausgabe davor ist das keine Meldung wert.
        now += TimeSpan.FromSeconds(60);
        shell.CheckSilence();
        Assert.Empty(notices);

        Output(first);
        shell.CheckSilence();
        now += TimeSpan.FromSeconds(shell.SilenceSeconds - 1);
        shell.CheckSilence();
        Assert.Empty(notices);

        now += TimeSpan.FromSeconds(1);
        shell.CheckSilence();
        Assert.Equal((first.Profile.Name, L.Get("NotifySilence", shell.SilenceSeconds)), Assert.Single(notices));
        Assert.True(first.HasSilence);

        // Einmal je Stille.
        now += TimeSpan.FromSeconds(60);
        shell.CheckSilence();
        Assert.Single(notices);

        // Neue Ausgabe macht wieder scharf.
        Output(first);
        shell.CheckSilence();
        now += TimeSpan.FromSeconds(shell.SilenceSeconds);
        shell.CheckSilence();
        Assert.Equal(2, notices.Count);

        // Ansehen löscht die Marke; abhaken beendet das Achten.
        shell.ActiveSession = first;
        Assert.False(first.HasSilence);
        shell.ToggleSilenceWatch(first);
        Assert.False(first.WatchesSilence);
        Output(first);
        now += TimeSpan.FromSeconds(600);
        shell.CheckSilence();
        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public void The_commands_switch_watching_on_and_off_and_the_seconds_stay_in_range()
    {
        var (shell, _, second) = TwoTabs();
        var watch = shell.Commands.Single(command => command.Id == "session.watchSilence");
        var unwatch = shell.Commands.Single(command => command.Id == "session.unwatchSilence");
        Assert.True(watch.IsAvailable);
        Assert.False(unwatch.IsAvailable);

        watch.Run();
        Assert.True(second.WatchesSilence);
        Assert.False(watch.IsAvailable);
        Assert.True(unwatch.IsAvailable);

        shell.SilenceSeconds = 1;
        Assert.Equal(ShellViewModel.MinSilenceSeconds, shell.SilenceSeconds);
        shell.SilenceSeconds = 99_999;
        Assert.Equal(ShellViewModel.MaxSilenceSeconds, shell.SilenceSeconds);
        Assert.Equal(AppSettings.DefaultSilenceSeconds, new AppSettings().SilenceSeconds);
        Assert.True(new AppSettings().ActivityTab);
    }
}
