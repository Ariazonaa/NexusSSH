using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Mitschrift, Tippen an mehrere Terminals, Hinweise und der Dateibrowser, der der Shell folgt.</summary>
[Collection(LanguageCollection.Name)]
public sealed class TerminalExtrasTests
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);

    private static string Plain(params string[] chunks)
    {
        var text = new PlainText();
        using var target = new MemoryStream();
        foreach (var chunk in chunks)
        {
            text.Append(Encoding.UTF8.GetBytes(chunk), target);
        }

        return Encoding.UTF8.GetString(target.ToArray());
    }

    [Fact]
    public void A_log_keeps_the_text_and_drops_colors_cursor_moves_and_titles()
    {
        Assert.Equal("total 4\nhäuser.txt\n", Plain("\e[1;32mtotal 4\e[0m\r\n", "häuser.txt\r\n"));
        Assert.Equal("a\tb", Plain("a\tb\a\b"));

        // Fenstertitel (OSC) bis BEL oder ESC \ – und nichts davon im Text.
        Assert.Equal("$ ls", Plain("\e]0;user@host: ~\a$ ls"));
        Assert.Equal("$ ls", Plain("\e]7;file://host/home/user\e\\$ ls"));

        // Eine Folge, die mitten im Block endet, wird im nächsten zu Ende gelesen.
        Assert.Equal("rot und danach", Plain("\e[3", "1mrot\e", "[0m und danach"));
        Assert.Equal("vor nach", Plain("vor \e]0;tit", "el\e", "\\nach"));
    }

    [Fact]
    public void Logging_writes_what_the_server_prints_until_it_is_stopped()
    {
        L.Init(AppLanguage.English);
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "session.log");
        var backend = new ScriptedBackend(Profile);
        var session = new SessionViewModel(Profile, backend, new ImmediateDispatcher(), new FakePrompts());
        var terminal = new StringBuilder();
        session.Output += data => terminal.Append(Encoding.UTF8.GetString(data.Span));
        session.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        backend.Last.Emit("before\r\n");
        Assert.True(session.StartLogging(path));
        Assert.True(session.IsLogging);
        Assert.Contains("is being logged", terminal.ToString());

        backend.Last.Emit("\e[32m$ uptime\e[0m\r\n 12:00 up 3 days\r\n");
        session.Send("uptime\r");
        session.StopLogging();
        backend.Last.Emit("after\r\n");

        // Nur die Ausgabe seit dem Einschalten, als Text – nicht das Getippte, nicht die Hinweise der App.
        Assert.False(session.IsLogging);
        Assert.Equal("$ uptime\n 12:00 up 3 days\n", File.ReadAllText(path));

        // Ein zweites Mal hängt an; mit der Sitzung endet die Mitschrift.
        Assert.True(session.StartLogging(path));
        backend.Last.Emit("more\r\n");
        session.Dispose();
        Assert.Equal("$ uptime\n 12:00 up 3 days\nmore\n", File.ReadAllText(path));

        // Ein Pfad, der sich nicht öffnen lässt, wird gemeldet statt zu werfen.
        var other = new SessionViewModel(Profile, backend, new ImmediateDispatcher(), new FakePrompts());
        Assert.False(other.StartLogging(Path.Combine(folder.Path, "nope", "x.log")));
        Assert.False(other.IsLogging);
    }

    [Fact]
    public void Typing_reaches_every_terminal_of_the_split_view_only_while_broadcast_is_on()
    {
        var backend = new ScriptedBackend(Profile);
        var shell = TestShell.Create(backend);
        var first = shell.OpenSession(Profile);
        Assert.False(shell.RunCommand("terminal.broadcast"));

        first.Start();
        backend.Connections[0].Set(new ConnectionStatus(ConnectionState.Connected));
        var second = shell.Split(sideBySide: true)!;
        second.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        var lone = shell.OpenSession(Profile);
        lone.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        var (a, b, c) = (backend.Connections[0], backend.Connections[1], backend.Connections[2]);

        shell.ActiveSession = second;
        shell.Input(second, Encoding.UTF8.GetBytes("ls\r"));
        Assert.Equal((0, 1, 0), (a.Input.Count, b.Input.Count, c.Input.Count));

        Assert.True(shell.RunCommand("terminal.broadcast"));
        shell.Input(second, Encoding.UTF8.GetBytes("uptime\r"));
        Assert.Equal(["uptime\r"], a.Input);
        Assert.Equal(["ls\r", "uptime\r"], b.Input);
        Assert.Empty(c.Input);

        // Der Wechsel zu einem ungeteilten Tab schaltet es aus – zurück in der geteilten Ansicht bleibt es aus.
        shell.ActiveSession = lone;
        Assert.False(shell.BroadcastInput);
        shell.Input(lone, Encoding.UTF8.GetBytes("x"));
        shell.ActiveSession = first;
        shell.Input(first, Encoding.UTF8.GetBytes("y"));
        Assert.Equal(["uptime\r", "y"], a.Input);
        Assert.Equal(2, b.Input.Count);
        Assert.Equal(["x"], c.Input);
    }

    [Fact]
    public void The_page_reports_the_shells_directory_and_the_bell_only_in_a_safe_form()
    {
        Assert.Equal(new WebMessage.DirectoryChanged("s1", "/var/www/my site"), TerminalMessages.Parse("""{"type":"cwd","id":"s1","data":"/var/www/my site"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"cwd","id":"s1","data":"relative/path"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"cwd","id":"s1","data":"/tmp/a\nb"}"""));
        Assert.Null(TerminalMessages.Parse("""{"type":"cwd","id":"","data":"/tmp"}"""));
        Assert.Null(TerminalMessages.Parse($$"""{"type":"cwd","id":"s1","data":"/{{new string('a', 5000)}}"}"""));
        Assert.Equal(new WebMessage.Bell("s1"), TerminalMessages.Parse("""{"type":"bell","id":"s1"}"""));
    }

    [Fact]
    public void The_file_browser_follows_the_shell_when_switched_on_and_visible()
    {
        var shell = TestShell.Create(out _);
        var session = shell.OpenSession(shell.Profiles[0]);
        session.Start();
        shell.IsSftpVisible = true;
        var start = session.Files.CurrentPath;

        // Von Haus aus aus: Die Shell meldet ihren Ordner, der Browser bleibt, wo er ist.
        Assert.False(session.FollowsShell);
        Assert.False(shell.OnTerminalDirectory(session, "/var/www/html/"));
        Assert.Equal(start, session.Files.CurrentPath);

        // Eingeschaltet wechselt er gleich dorthin, wo die Shell steht, und folgt ihr dann.
        session.FollowsShell = true;
        Assert.Equal("/var/www/html", session.Files.CurrentPath);
        Assert.True(shell.OnTerminalDirectory(session, start));
        Assert.Equal(start, session.Files.CurrentPath);
        Assert.True(shell.OnTerminalDirectory(session, "/var/www/html/"));
        Assert.Equal("/var/www/html", session.Files.CurrentPath);

        // Schon dort: keine neue Anfrage. Ausgeschaltet oder ausgeblendet: der Browser bleibt, wo er ist.
        Assert.False(shell.OnTerminalDirectory(session, "/var/www/html"));
        session.FollowsShell = false;
        Assert.False(shell.OnTerminalDirectory(session, start));
        session.FollowsShell = true;
        Assert.Equal(start, session.Files.CurrentPath);
        shell.IsSftpVisible = false;
        Assert.False(shell.OnTerminalDirectory(session, "/var/www/html"));
        Assert.Equal(start, session.Files.CurrentPath);
    }

    [Fact]
    public void Following_the_terminal_folder_needs_a_shell_that_reports_it_and_holds_per_session()
    {
        var shell = TestShell.Create(out _);
        var first = shell.OpenSession(shell.Profiles[0]);
        var second = shell.OpenSession(shell.Profiles[1]);
        first.Start();
        second.Start();
        shell.IsSftpVisible = true;
        var start = first.Files.CurrentPath;
        var changed = new List<string?>();
        first.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Solange die Shell nichts meldet, lässt es sich nicht einschalten – die App tippt dafür nichts in die Shell.
        Assert.False(first.CanFollowShell);
        first.FollowsShell = true;
        Assert.False(first.FollowsShell);
        Assert.Equal(start, first.Files.CurrentPath);

        // Die erste Meldung macht es möglich (die Ansicht hört auf CanFollowShell).
        shell.OnTerminalDirectory(first, "/var/www/html");
        Assert.True(first.CanFollowShell);
        Assert.Contains(nameof(SessionViewModel.CanFollowShell), changed);
        first.FollowsShell = true;
        Assert.True(first.FollowsShell);
        Assert.Equal("/var/www/html", first.Files.CurrentPath);

        // Je Sitzung: die zweite folgt nicht mit.
        var other = second.Files.CurrentPath;
        Assert.False(second.FollowsShell);
        Assert.False(shell.OnTerminalDirectory(second, "/var/www/html"));
        Assert.Equal(other, second.Files.CurrentPath);
    }

    private sealed class FakeMonitor : IServerMonitor
    {
        public ServerStats? Current { get; private set; }

        public bool IsUnavailable => false;

        public event Action? Changed;

        public void SetActive(bool active)
        {
        }

        public Task InstallKeyAsync(string publicKey) => Task.CompletedTask;

        public void Measure(double cpu, double memory, long read, long written)
        {
            Current = new ServerStats(true, cpu, memory, 10, "Linux", "6.8", TimeSpan.FromHours(1), Io: new DiskIo(read, written, 0));
            Changed?.Invoke();
        }
    }

    [Fact]
    public void The_inspector_keeps_the_last_measurements_for_the_disk_curve()
    {
        var inspector = new ServerInspectorViewModel(Profile, new ImmediateDispatcher());
        var monitor = new FakeMonitor();
        inspector.Attach(monitor);
        inspector.SetWatched(true);
        Assert.Empty(inspector.DiskIoHistory);

        monitor.Measure(cpu: 25, memory: 50, read: 1000, written: 0);
        monitor.Measure(cpu: 100, memory: 40, read: 3000, written: 1000);

        // Der Durchsatz hat keine feste Obergrenze: gemessen am höchsten Wert im Zeitraum.
        Assert.Equal([0.25, 1.0], inspector.DiskIoHistory);

        // Bloßes Auffrischen der Anzeige ist keine Messung.
        inspector.SetWatched(false);
        inspector.SetWatched(true);
        Assert.Equal(2, inspector.DiskIoHistory.Count);

        // Nur die letzten Messungen bleiben, und eine neue Verbindung beginnt leer.
        for (var index = 0; index < ServerInspectorViewModel.HistoryLength + 5; index++)
        {
            monitor.Measure(cpu: 0, memory: 0, read: index, written: 0);
        }

        Assert.Equal(ServerInspectorViewModel.HistoryLength, inspector.DiskIoHistory.Count);
        Assert.Equal(5.0 / 64, inspector.DiskIoHistory[0], precision: 6);
        inspector.Attach(new FakeMonitor());
        Assert.Empty(inspector.DiskIoHistory);
    }

    [Fact]
    public async Task Entries_are_packed_and_archives_unpacked_on_the_server()
    {
        var files = new MockRemoteFileSystem("/var/www", new MockTransferQueue(), "web01")
            .File("/var/www/html/index.html", 100, hoursAgo: 1)
            .File("/var/www/nginx.conf", 200, hoursAgo: 2);
        var browser = new SftpBrowserViewModel(files);
        await browser.LoadAsync();

        // Ohne Endung wird es ein .tar.gz; das neue Archiv steht danach in der Liste.
        Assert.True(await browser.PackAsync([.. browser.Entries], " site "));
        Assert.Equal(["pack /var/www/site.tar.gz: html, nginx.conf"], files.Archived);
        Assert.Contains(browser.Entries, entry => entry.Name == "site.tar.gz");

        // Ein vorhandenes Archiv bleibt, und der Grund steht da.
        Assert.False(await browser.PackAsync([browser.Entries[0]], "site.tar.gz"));
        Assert.StartsWith("The command on the server failed.", browser.Error);
        Assert.False(await browser.PackAsync([], "empty"));

        var archive = browser.Entries.Single(entry => entry.Name == "site.tar.gz");
        Assert.True(SftpBrowserViewModel.IsArchive(archive));
        Assert.False(SftpBrowserViewModel.IsArchive(browser.Entries.Single(entry => entry.Name == "nginx.conf")));
        Assert.True(await browser.UnpackAsync(archive));
        Assert.Equal("unpack /var/www/site.tar.gz", files.Archived[^1]);

        Assert.Equal(("a.tar.gz", "b.tgz", "c.zip.tar.gz"), (SftpBrowserViewModel.ArchiveName("a"), SftpBrowserViewModel.ArchiveName("b.tgz"), SftpBrowserViewModel.ArchiveName("c.zip")));
        Assert.False(await browser.PackAsync([browser.Entries[0]], "a/b"));
    }

    [Fact]
    public async Task The_core_is_asked_to_pack_unpack_and_measure_a_folder()
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[{"id":"p1","name":"web01","host":"h","port":22,"user":"u","group":"","tags":[],"favorite":false,"use_agent":true}]}""";
        using var backend = new Backend.CoreBackend(transport);
        var connection = backend.Connect(backend.GetProfiles()[0]);
        connection.Terminal.Start();
        transport.Raise("""{"event":"session.state","session":1,"state":"connected","home":"/"}""");

        _ = connection.Files!.PackAsync("/var/www", ["html", "nginx.conf"], "site.tar.gz");
        var pack = Assert.Single(transport.Sent("sftp.pack"));
        Assert.Equal(("/var/www", "site.tar.gz", 2), (pack.GetProperty("directory").GetString(), pack.GetProperty("archive").GetString(), pack.GetProperty("names").GetArrayLength()));

        _ = connection.Files.UnpackAsync("/var/www", "site.tar.gz");
        Assert.Equal("site.tar.gz", Assert.Single(transport.Sent("sftp.unpack")).GetProperty("archive").GetString());

        var measuring = connection.Files.FolderSizeAsync("/var/www");
        var size = Assert.Single(transport.Sent("sftp.size"));
        Assert.Equal("/var/www", size.GetProperty("path").GetString());
        transport.Raise($$"""{"event":"sftp.result","request":{{size.GetProperty("request").GetUInt64()}},"ok":true,"size":2097152}""");
        Assert.Equal(2_097_152, await measuring);
    }

    [Fact]
    public void Finished_transfers_and_the_bell_ask_for_attention_unless_switched_off()
    {
        var shell = TestShell.Create(out var backend);
        var queue = (MockTransferQueue)backend.Transfers;
        var notes = new List<(string Title, string Text)>();
        shell.AttentionRequested += (title, text) => notes.Add((title, text));
        var session = shell.OpenSession(shell.Profiles[0]);

        queue.Complete("t1");
        queue.Fail("t3", "permission_denied");
        shell.OnTerminalBell(session);

        Assert.Equal(
            [("web01", "backup.tar.gz has been transferred."), ("database01", "database.sql could not be transferred."), ("web01", "The terminal rang the bell.")],
            notes);

        // Was beim Start schon fertig war (nginx.conf), meldet sich nicht.
        Assert.DoesNotContain(notes, note => note.Text.Contains("nginx.conf", StringComparison.Ordinal));

        shell.Notifications = false;
        shell.OnTerminalBell(session);
        Assert.Equal(3, notes.Count);
    }

    [Fact]
    public void Broadcast_reaches_only_the_zoomed_pane_while_zoomed_and_everyone_again_afterwards()
    {
        var backend = new ScriptedBackend(Profile);
        var shell = TestShell.Create(backend);
        var first = shell.OpenSession(Profile);
        first.Start();
        backend.Connections[0].Set(new ConnectionStatus(ConnectionState.Connected));
        var second = shell.Split(sideBySide: true)!;
        second.Start();
        backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        var (a, b) = (backend.Connections[0], backend.Connections[1]);
        Assert.True(shell.RunCommand("terminal.broadcast"));

        shell.ToggleZoom();
        shell.Input(second, Encoding.UTF8.GetBytes("zoomed\r"));
        Assert.Empty(a.Input);
        Assert.Equal(["zoomed\r"], b.Input);

        shell.ToggleZoom();
        Assert.True(shell.BroadcastInput);
        shell.Input(second, Encoding.UTF8.GetBytes("all\r"));
        Assert.Equal(["all\r"], a.Input);
        Assert.Equal(["zoomed\r", "all\r"], b.Input);
    }
}
