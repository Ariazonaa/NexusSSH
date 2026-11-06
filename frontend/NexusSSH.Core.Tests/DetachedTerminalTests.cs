using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Das Terminal eines Tabs in einem eigenen Fenster: Lösen, Zurückholen, Neustart – und die Ausgabe unterwegs.</summary>
[Collection(LanguageCollection.Name)]
public sealed class DetachedTerminalTests
{
    public DetachedTerminalTests() => L.Init(AppLanguage.English);

    private static (ShellViewModel Shell, List<SessionViewModel> Tabs, List<string> Moves) Open(int count) => Open(count, null, out _);

    private static (ShellViewModel Shell, List<SessionViewModel> Tabs, List<string> Moves) Open(int count, AppSettings? settings, out MockBackend backend)
    {
        var shell = TestShell.Create(out backend, settings);
        var profiles = backend.Profiles.GetProfiles();
        var tabs = Enumerable.Range(0, count).Select(index => shell.OpenSession(profiles[index % profiles.Count])).ToList();
        var moves = new List<string>();
        shell.DetachedChanged += session => moves.Add($"{tabs.IndexOf(session)}:{session.IsDetached}");
        return (shell, tabs, moves);
    }

    [Fact]
    public void A_detached_tab_stays_in_the_list_and_the_terminal_area_shows_a_note_instead()
    {
        var (shell, tabs, moves) = Open(2);
        shell.ActiveSession = tabs[0];

        Assert.True(shell.Detach(tabs[0], new WindowBounds(10, 20, 900, 600)));

        Assert.Equal(tabs, shell.Sessions);
        Assert.True(tabs[0].IsDetached);
        Assert.Equal(new WindowBounds(10, 20, 900, 600), tabs[0].DetachedBounds);
        Assert.Equal(["0:True"], moves);
        Assert.Empty(shell.Layout.Panes);
        Assert.True(shell.ShowsDetachedNote);

        // Ein anderer Tab zeigt sein Terminal wie immer; ein zweites Mal lässt sich derselbe nicht lösen.
        shell.ActiveSession = tabs[1];
        Assert.Equal([tabs[1]], shell.Layout.Panes);
        Assert.False(shell.ShowsDetachedNote);
        Assert.False(shell.Detach(tabs[0]));

        // Zurückgeholt ist er der aktive Tab und zeigt sein Terminal wieder.
        Assert.True(shell.Reattach(tabs[0]));
        Assert.Equal(["0:True", "0:False"], moves);
        Assert.Same(tabs[0], shell.ActiveSession);
        Assert.Equal([tabs[0]], shell.Layout.Panes);
        Assert.False(shell.Reattach(tabs[0]));
    }

    [Fact]
    public void Detaching_takes_the_tab_out_of_its_split_and_a_detached_one_cannot_be_split()
    {
        var (shell, tabs, moves) = Open(1);
        var second = shell.Split(sideBySide: true)!;

        Assert.True(shell.Detach(second));

        Assert.False(tabs[0].IsSplit);
        Assert.False(second.IsSplit);
        Assert.False(shell.CanSplit);
        Assert.Null(shell.Split(sideBySide: true));

        // Neben einen anderen gestellt, kommt sein Terminal dafür zurück.
        shell.ActiveSession = tabs[0];
        shell.SplitWith(second, sideBySide: true);
        Assert.False(second.IsDetached);
        Assert.Equal([tabs[0], second], shell.Layout.Panes);
        Assert.Equal(2, moves.Count);
    }

    [Fact]
    public void The_commands_are_offered_only_where_they_apply()
    {
        var (shell, tabs, _) = Open(1);
        ShellCommand Command(string id) => shell.Commands.Single(command => command.Id == id);

        Assert.True(Command("session.detach").IsAvailable);
        Assert.False(Command("session.reattach").IsAvailable);

        Command("session.detach").Run();

        Assert.True(tabs[0].IsDetached);
        Assert.False(Command("session.detach").IsAvailable);
        Assert.True(Command("session.reattach").IsAvailable);

        // Mit der Sitzung schließt auch ihr Fenster – die App hört dafür auf die Liste der Sitzungen.
        shell.CloseSession(tabs[0]);
        Assert.Empty(shell.Sessions);
    }

    [Fact]
    public void Detached_tabs_and_where_their_windows_stood_come_back_after_a_restart()
    {
        var settings = new AppSettings { RestoreTabs = true };
        var (shell, tabs, _) = Open(3, settings, out var backend);
        shell.Detach(tabs[1], new WindowBounds(-1600, 40, 1000, 700));
        shell.Detach(tabs[2]);
        shell.ActiveSession = tabs[0];
        shell.RememberOpenSessions();

        Assert.Equal([new SavedWindow(1, new WindowBounds(-1600, 40, 1000, 700)), new SavedWindow(2, null)], settings.DetachedTabs);

        var again = new ShellViewModel(backend, new ImmediateDispatcher(), settings, new FakePrompts());
        var opened = new List<SessionViewModel>();
        again.DetachedChanged += opened.Add;
        again.RestoreOpenSessions();

        Assert.Equal([false, true, true], again.Sessions.Select(session => session.IsDetached));
        Assert.Equal(new WindowBounds(-1600, 40, 1000, 700), again.Sessions[1].DetachedBounds);
        Assert.Equal([again.Sessions[1], again.Sessions[2]], opened);
        Assert.Equal([again.Sessions[0]], again.Layout.Panes);

        // Ohne „Tabs wiederherstellen“ bleibt davon nichts in den Einstellungen.
        again.RestoreTabs = false;
        again.RememberOpenSessions();
        Assert.Empty(settings.DetachedTabs);
    }

    [Fact]
    public void Settings_drop_windows_that_point_nowhere_or_have_no_usable_size()
    {
        var settings = new AppSettings
        {
            OpenTabs = ["a", "b"],
            DetachedTabs =
            [
                new SavedWindow(0, new WindowBounds(0, 0, 5, 5)),
                new SavedWindow(1, new WindowBounds(100, 100, 900, 600)),
                new SavedWindow(1, null),
                new SavedWindow(7, null),
                new SavedWindow(-1, null),
                null!,
            ],
        }.Sanitize();

        Assert.Equal([new SavedWindow(0, null), new SavedWindow(1, new WindowBounds(100, 100, 900, 600))], settings.DetachedTabs);
    }

    [Fact]
    public async Task Output_during_a_move_arrives_once_and_after_the_content_of_the_old_terminal()
    {
        var terminal = new FakeTerminal();
        using var feed = new TerminalFeed(terminal);
        var first = new StringBuilder();
        var second = new StringBuilder();

        // Vor dem ersten Terminal wird aufgehoben, was kommt.
        terminal.Emit("a");
        Assert.False(feed.IsAttached);
        feed.Attach(data => first.Append(Encoding.UTF8.GetString(data.Span)));
        terminal.Emit("b");
        Assert.Equal("ab", first.ToString());
        Assert.True(feed.Moved.IsCompletedSuccessfully);

        // Umzug: Das alte Terminal bekommt nichts mehr, das neue wartet auf dessen Inhalt.
        feed.BeginMove();
        terminal.Emit("c");
        var moved = feed.Moved;
        Assert.False(moved.IsCompleted);
        terminal.Emit("d");
        feed.CompleteMove(Encoding.UTF8.GetBytes("[ab]"));
        feed.CompleteMove(Encoding.UTF8.GetBytes("zu spät"));
        var content = await moved;
        Assert.Equal("[ab]", Encoding.UTF8.GetString(content));

        feed.Attach(data => second.Append(Encoding.UTF8.GetString(data.Span)), content);
        terminal.Emit("e");

        Assert.Equal("ab", first.ToString());
        Assert.Equal("[ab]cde", second.ToString());
        Assert.True(feed.Moved.IsCompletedSuccessfully);
        Assert.Empty(await feed.Moved);
    }

    [Fact]
    public async Task A_feed_that_ends_stops_listening_and_releases_whoever_waits_for_the_move()
    {
        var terminal = new FakeTerminal();
        var feed = new TerminalFeed(terminal);
        feed.BeginMove();
        var moved = feed.Moved;

        feed.Dispose();

        Assert.Empty(await moved);
        Assert.False(terminal.HasListeners);
    }

    [Fact]
    public void What_is_held_during_a_move_has_an_upper_limit()
    {
        var terminal = new FakeTerminal();
        using var feed = new TerminalFeed(terminal);
        var block = new byte[TerminalFeed.MaxHeld / 2 + 1];

        terminal.Emit(block);
        terminal.Emit(block);
        terminal.Emit([1, 2, 3]);
        long received = 0;
        feed.Attach(data => received += data.Length);

        // Über der Grenze zählt das Neueste: der zweite Block und was danach kam.
        Assert.Equal(block.Length + 3, received);
    }

    [Fact]
    public void A_moving_session_takes_what_still_waits_in_the_pump_and_runs_on()
    {
        var posted = new List<string>();
        var flow = new List<(string Id, bool Paused)>();
        var scheduled = new Queue<Action>();
        var pump = new TerminalOutputPump((id, data) => posted.Add(id), scheduled.Enqueue, (id, paused) => flow.Add((id, paused)));

        // Über der Marke hält die Pumpe die Sitzung an; dann kommt noch etwas, das nicht mehr gezeichnet wird.
        pump.Add("a", new byte[TerminalOutputPump.HighWater + 1]);
        scheduled.Dequeue()();
        Assert.Equal([("a", true)], flow);
        pump.Add("a", "rest"u8);
        pump.Add("b", "other"u8);

        Assert.Equal("rest"u8.ToArray(), pump.Take("a"));

        Assert.Equal([("a", true), ("a", false)], flow);
        Assert.Equal(0, pump.Backlog("a"));
        Assert.Empty(pump.Take("a"));
        Assert.Equal(5, pump.Backlog("b"));
    }

    [Fact]
    public void The_page_names_the_content_of_a_terminal_only_up_to_a_limit()
    {
        Assert.Equal("""{"type":"snapshot","id":"s1"}""", TerminalMessages.Snapshot("s1"));

        // Der Inhalt kommt unverändert an – samt Steuerzeichen, er geht nur wieder in ein Terminal.
        var parsed = Assert.IsType<WebMessage.Snapshot>(TerminalMessages.Parse("""{"type":"snapshot","id":"s1","data":"a\u001b[31mb\r\n\u001b[?1049h"}"""));
        Assert.Equal(("s1", "a\u001b[31mb\r\n\u001b[?1049h"), (parsed.Id, parsed.Data));
        Assert.IsType<WebMessage.Snapshot>(TerminalMessages.Parse("""{"type":"snapshot","id":"s1","data":""}"""));

        Assert.Null(TerminalMessages.Parse("""{"type":"snapshot","id":"","data":"x"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"snapshot","id":"s1"}"""));
        var tooLong = new string('x', TerminalMessages.MaxSnapshotLength + 1);
        Assert.Null(TerminalMessages.Parse($$"""{"type":"snapshot","id":"s1","data":"{{tooLong}}"}"""));
    }

    private sealed class FakeTerminal : ITerminalSession
    {
        public event Action<ReadOnlyMemory<byte>>? Output;

        public bool HasListeners => Output is not null;

        public void Emit(string text) => Output?.Invoke(Encoding.UTF8.GetBytes(text));

        public void Emit(byte[] data) => Output?.Invoke(data);

        public void Start()
        {
        }

        public void Write(ReadOnlyMemory<byte> input)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void SetPaused(bool paused)
        {
        }
    }

    [Fact]
    public void Detaching_a_pane_of_a_tree_closes_the_gap_and_keeps_the_rest_arranged()
    {
        var (shell, tabs, _) = Open(1);
        var second = shell.Split(sideBySide: true)!;
        var third = shell.Split(sideBySide: false)!;
        var fourth = shell.Split(sideBySide: true)!;

        Assert.True(shell.Detach(third));

        Assert.True(third.IsDetached);
        Assert.False(third.IsSplit);
        shell.ActiveSession = tabs[0];
        Assert.Equal([tabs[0], second, fourth], shell.Layout.Panes);
        var root = Assert.IsType<SplitBranch<SessionViewModel>>(shell.Layout.Tree);
        Assert.True(root.SideBySide);
        Assert.Equal(2, root.Children.Count);
        Assert.False(Assert.IsType<SplitBranch<SessionViewModel>>(root.Children[1]).SideBySide);
        Assert.Equal(1.0, root.Shares.Sum(), 6);
    }
}
