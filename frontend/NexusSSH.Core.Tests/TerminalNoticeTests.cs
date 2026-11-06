using NexusSSH.Core;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Meldungen eines Programms im Terminal (OSC 9, OSC 777): ein Hinweis von Windows und eine Marke am Reiter.</summary>
[Collection(LanguageCollection.Name)]
public class TerminalNoticeTests
{
    private static (ShellViewModel Shell, SessionViewModel First, SessionViewModel Second, List<(string Title, string Text)> Notes) Setup()
    {
        var shell = TestShell.Create(out var backend);
        var first = shell.OpenSession(backend.Profiles.GetProfiles()[0]);
        var second = shell.OpenSession(backend.Profiles.GetProfiles()[1]);
        var notes = new List<(string, string)>();
        shell.AttentionRequested += (title, text) => notes.Add((title, text));
        return (shell, first, second, notes);
    }

    [Fact]
    public void A_notice_from_the_page_is_read_and_cut_short()
    {
        var long_ = new string('x', 300);
        var message = Assert.IsType<WebMessage.Notice>(TerminalMessages.Parse(
            $$"""{"type":"notify","id":"s1","title":"{{long_}}","body":"fertig\u001b[31m ‮gnp.exe⁦"}"""));

        Assert.Equal("s1", message.Id);
        Assert.Equal(TerminalMessages.MaxNoticeTitleLength, message.Title.Length);
        Assert.EndsWith("…", message.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(message.Body.AsEnumerable(), Format.IsInvisible);
        Assert.StartsWith("fertig[31m ", message.Body, StringComparison.Ordinal);

        var body = Assert.IsType<WebMessage.Notice>(TerminalMessages.Parse($$"""{"type":"notify","id":"s1","body":"{{long_}}"}"""));
        Assert.Equal("", body.Title);
        Assert.Equal(TerminalMessages.MaxNoticeBodyLength, body.Body.Length);

        // Ohne Sitzung, ohne Text und mit Text, der nur aus Steuerzeichen besteht: keine Meldung.
        Assert.Null(TerminalMessages.Parse("""{"type":"notify","title":"a","body":"b"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"notify","id":"","title":"a","body":"b"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"notify","id":"s1"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"notify","id":"s1","title":"","body":""}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"notify","id":"s1","title":"\u0007","body":"\u001b ‮"}"""));
    }

    [Fact]
    public void The_page_and_core_agree_on_what_a_notice_is()
    {
        // Die Seite selbst läuft in keinem Test hier; sie wird am Quelltext festgehalten: dieselben Namen, dieselben
        // Grenzen, und OSC 9;4 (Fortschritt) ist keine Meldung.
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "App", "Assets", "terminal", "terminal.js"));

        Assert.Contains("for (const code of [9, 777])", page, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'notify', id, title: notice.title, body: notice.body })", page, StringComparison.Ordinal);
        Assert.Contains($"tidy(title, {TerminalMessages.MaxNoticeTitleLength})", page, StringComparison.Ordinal);
        Assert.Contains($"tidy(body, {TerminalMessages.MaxNoticeBodyLength})", page, StringComparison.Ordinal);
        Assert.Contains("data.startsWith('notify;')", page, StringComparison.Ordinal);
        Assert.Contains("/^(?:[1-9]|1[0-2])(?:;|$)/.test(data)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_semicolon_in_the_text_stays_in_the_text()
    {
        // Was die Seite aus „notify;Build;3 Fehler; 2 Warnungen“ macht: der Titel bis zum nächsten „;“, der Rest ist der Text.
        var message = Assert.IsType<WebMessage.Notice>(TerminalMessages.Parse(
            """{"type":"notify","id":"s1","title":"Build","body":"3 Fehler; 2 Warnungen"}"""));
        Assert.Equal(("Build", "3 Fehler; 2 Warnungen"), (message.Title, message.Body));
    }

    [Fact]
    public void A_notice_marks_the_tab_and_tells_windows_with_the_name_of_the_server()
    {
        var (shell, first, second, notes) = Setup();
        Assert.Same(second, shell.ActiveSession);

        shell.OnTerminalNotice(first, "Build", "fertig");

        Assert.True(first.HasBell);
        Assert.Equal([($"{first.Profile.Name}: Build", "fertig")], notes);

        // Der aktive Tab bekommt keine Marke (der Benutzer sieht ihn ja).
        shell.OnTerminalNotice(second, "x", "y");
        Assert.False(second.HasBell);

        shell.ActiveSession = first;
        Assert.False(first.HasBell);
    }

    [Fact]
    public void A_notice_without_a_title_or_without_text_still_names_the_server()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var (shell, first, _, notes) = Setup();
        shell.Now = () => now;

        shell.OnTerminalNotice(first, "", "Build fertig");
        now += ShellViewModel.BellPause;
        shell.OnTerminalNotice(first, "Build", "");

        Assert.Equal([(first.Profile.Name, "Build fertig"), (first.Profile.Name, "Build")], notes);
    }

    [Fact]
    public void A_notice_cannot_pose_as_a_notice_of_the_app()
    {
        // Der Name des Servers steht immer davor, und was der Server schickt, ist auch hier ohne Steuerzeichen.
        var (shell, first, _, notes) = Setup();

        shell.OnTerminalNotice(first, "NexusSSH\n", "Update‮ verfügbar");

        var (title, text) = Assert.Single(notes);
        Assert.StartsWith($"{first.Profile.Name}: ", title, StringComparison.Ordinal);
        Assert.DoesNotContain((title + text).AsEnumerable(), Format.IsInvisible);
    }

    [Fact]
    public void Notices_and_the_bell_share_one_pause()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var (shell, first, _, notes) = Setup();
        shell.Now = () => now;

        shell.OnTerminalBell(first);
        now += TimeSpan.FromSeconds(1);
        shell.OnTerminalNotice(first, "Build", "fertig");
        Assert.Single(notes);

        now += ShellViewModel.BellPause;
        shell.OnTerminalNotice(first, "Build", "zweimal");
        Assert.Equal(2, notes.Count);

        // Und umgekehrt.
        now += TimeSpan.FromSeconds(1);
        shell.OnTerminalBell(first);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void A_server_that_sends_notices_in_a_loop_gets_one_notice_per_pause()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var (shell, first, _, notes) = Setup();
        shell.Now = () => now;

        for (var round = 0; round < 10_000; round++)
        {
            shell.OnTerminalNotice(first, "t" + round, "b" + round);
        }

        Assert.Single(notes);
        Assert.True(first.HasBell);

        now += ShellViewModel.BellPause;
        shell.OnTerminalNotice(first, "next", "one");
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void Switched_off_there_is_neither_mark_nor_notice()
    {
        var (shell, first, _, notes) = Setup();
        Assert.True(shell.TerminalNotices);

        shell.TerminalNotices = false;
        shell.OnTerminalNotice(first, "Build", "fertig");

        Assert.False(first.HasBell);
        Assert.Empty(notes);
        Assert.False(shell.Settings.TerminalNotices);

        // Die Pause hat die verworfene Meldung nicht verbraucht.
        shell.TerminalNotices = true;
        shell.OnTerminalNotice(first, "Build", "fertig");
        Assert.Single(notes);
    }

    [Fact]
    public void Without_windows_notifications_the_tab_is_still_marked()
    {
        var (shell, first, _, notes) = Setup();
        shell.Notifications = false;

        shell.OnTerminalNotice(first, "Build", "fertig");

        Assert.True(first.HasBell);
        Assert.Empty(notes);
    }
}
