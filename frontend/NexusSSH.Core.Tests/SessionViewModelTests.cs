using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

[Collection(LanguageCollection.Name)]
public sealed class SessionViewModelTests
{
    private static readonly ServerProfile Profile = new("p1", "web01", "10.0.1.10", 22, "ubuntu", "Production", []);

    private readonly ScriptedBackend _backend = new(Profile);
    private readonly FakePrompts _prompts = new();
    private readonly StringBuilder _terminal = new();

    public SessionViewModelTests() => L.Init(AppLanguage.English);

    private SessionViewModel Create()
    {
        var session = new SessionViewModel(Profile, _backend, new ImmediateDispatcher(), _prompts);
        session.Output += data => _terminal.Append(Encoding.UTF8.GetString(data.Span));
        return session;
    }

    [Fact]
    public void Nothing_connects_until_the_terminal_starts_and_then_with_its_size()
    {
        var session = Create();
        var connection = _backend.Last;
        Assert.False(connection.Started);
        Assert.Equal(ConnectionState.Idle, session.State);

        session.Resize(132, 43);
        session.Start();
        session.Start();

        Assert.Equal(1, connection.Starts);
        Assert.Equal((132, 43), connection.Size);
    }

    [Fact]
    public void The_state_is_shown_in_words_and_announced_in_the_terminal()
    {
        var session = Create();
        session.Start();

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connecting));
        Assert.True(session.IsBusy);
        Assert.Equal("Connecting …", session.StatusText);
        Assert.Contains("Connecting to ubuntu@10.0.1.10 …", _terminal.ToString());

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Authenticating));
        Assert.Equal("Signing in …", session.StatusText);

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.False(session.IsBusy);
        Assert.False(session.IsClosed);
        Assert.True(session.Inspector.IsOnline);
        Assert.Equal("Online", session.Inspector.StatusText);
    }

    [Fact]
    public void The_header_names_the_state_in_a_word()
    {
        var session = Create();
        Assert.Equal("Disconnected", session.StateLabel);
        session.Start();

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connecting));
        Assert.Equal("Connecting …", session.StateLabel);

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Authenticating));
        Assert.Equal("Signing in …", session.StateLabel);

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.Equal("Connected", session.StateLabel);

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected, FilesOnly: true));
        Assert.Equal("Files only", session.StateLabel);

        // Warum sie endete, steht in der Hinweisleiste; hier nur, dass.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.AuthFailed, "no sign-in method was accepted"));
        Assert.Equal("Disconnected", session.StateLabel);
    }

    [Fact]
    public void Disconnecting_ends_the_connection_and_keeps_the_tab_for_reconnecting()
    {
        var session = Create();

        // Vor dem Start gibt es nichts zu trennen.
        session.Disconnect();
        Assert.False(_backend.Last.Disposed);

        session.Start();
        var connection = _backend.Last;
        connection.Set(new ConnectionStatus(ConnectionState.Connected));
        var ended = new List<CloseReason>();
        session.ConnectionEnded += (_, status) => ended.Add(status.Reason);

        session.Disconnect();

        Assert.True(connection.Disposed);
        Assert.True(session.IsClosed);
        Assert.False(session.Inspector.IsOnline);
        Assert.Equal("Connection closed.", session.StatusText);
        Assert.Contains("Connection closed.", _terminal.ToString());
        Assert.Equal([CloseReason.User], ended);

        // Was die alte Verbindung danach noch meldet, gilt nicht mehr; ein zweites Trennen tut nichts.
        connection.Set(new ConnectionStatus(ConnectionState.Connected));
        session.Disconnect();
        Assert.True(session.IsClosed);
        Assert.Single(ended);

        session.Reconnect();
        Assert.NotSame(connection, _backend.Last);
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.Equal("Connected", session.StateLabel);
    }

    /// <summary>Führt „später“ erst aus, wenn der Test es sagt, und merkt sich die Wartezeiten.</summary>
    private sealed class ManualSchedule
    {
        private readonly List<(TimeSpan Delay, Action Run, bool[] Cancelled)> _pending = [];

        public List<TimeSpan> Delays { get; } = [];

        public bool Enabled { get; set; } = true;

        /// <summary>Die Uhr, nach der der Reiter die Sekunden bis zum nächsten Versuch zählt.</summary>
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        public int Waiting => _pending.Count(entry => !entry.Cancelled[0]);

        public SessionRetry Retry => new(() => Enabled, (delay, run) =>
        {
            var cancelled = new[] { false };
            Delays.Add(delay);
            _pending.Add((delay, run, cancelled));
            return new Cancel(() => cancelled[0] = true);
        }, () => Now);

        /// <summary>Die Wartezeit ist um: führt aus, was nicht abgesagt wurde.</summary>
        public void Elapse()
        {
            var due = _pending.ToList();
            _pending.Clear();
            foreach (var (_, run, cancelled) in due)
            {
                if (!cancelled[0])
                {
                    run();
                }
            }
        }

        private sealed class Cancel(Action cancel) : IDisposable
        {
            public void Dispose() => cancel();
        }
    }

    private SessionViewModel Create(ManualSchedule schedule)
    {
        var session = new SessionViewModel(Profile, _backend, new ImmediateDispatcher(), _prompts, retry: schedule.Retry);
        session.Output += data => _terminal.Append(Encoding.UTF8.GetString(data.Span));
        return session;
    }

    [Fact]
    public void A_lost_connection_is_rebuilt_by_itself_with_growing_pauses()
    {
        var schedule = new ManualSchedule();
        var session = Create(schedule);
        session.Start();
        var first = _backend.Last;
        first.Set(new ConnectionStatus(ConnectionState.Connected));

        first.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));

        Assert.Equal([TimeSpan.FromSeconds(2)], schedule.Delays);
        Assert.Contains("Reconnecting in 2 s (attempt 1 of 8)", _terminal.ToString());
        Assert.Same(first, _backend.Last);

        // Der Server ist noch weg: nächster Versuch, längere Pause.
        schedule.Elapse();
        var second = _backend.Last;
        Assert.NotSame(first, second);
        Assert.True(second.Started);
        second.Set(new ConnectionStatus(ConnectionState.Connecting));
        second.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed, "connection refused"));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], schedule.Delays);

        // Er ist wieder da: Die Reihe beginnt beim nächsten Abriss von vorn.
        schedule.Elapse();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.Equal(0, schedule.Waiting);
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.Delays[^1]);
    }

    /// <summary>Der Reiter sagt, was die Sitzung von selbst versucht – ohne dass man den Tab öffnen muss.</summary>
    [Fact]
    public void The_tab_says_which_attempt_comes_when_and_falls_silent_once_it_is_over()
    {
        var schedule = new ManualSchedule();
        var session = Create(schedule);
        var changes = new List<string>();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.RetryText))
            {
                changes.Add(session.RetryText);
            }
        };
        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.Equal(string.Empty, session.RetryText);

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.Equal("attempt 1 in 2 s", session.RetryText);

        // Die Sekunden zählt die Oberfläche herunter; nie unter null.
        schedule.Now += TimeSpan.FromMilliseconds(1200);
        session.RefreshRetry();
        Assert.Equal("attempt 1 in 1 s", session.RetryText);
        schedule.Now += TimeSpan.FromSeconds(5);
        session.RefreshRetry();
        Assert.Equal("attempt 1 in 0 s", session.RetryText);

        // Der Versuch läuft, scheitert, der nächste wartet länger.
        schedule.Elapse();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connecting));
        Assert.Equal("attempt 1…", session.RetryText);
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed, "connection refused"));
        Assert.Equal("attempt 2 in 5 s", session.RetryText);

        // Wieder verbunden: nichts mehr zu sagen.
        schedule.Elapse();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.Equal(string.Empty, session.RetryText);
        Assert.Equal(string.Empty, changes[^1]);

        // Von Hand neu verbunden, während ein Versuch wartet: Das ist keiner der selbsttätigen mehr.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.Equal("attempt 1 in 2 s", session.RetryText);
        session.Reconnect();
        Assert.Equal(string.Empty, session.RetryText);
    }

    [Theory]
    [InlineData(CloseReason.User)]
    [InlineData(CloseReason.Remote)]
    [InlineData(CloseReason.AuthFailed)]
    [InlineData(CloseReason.HostKeyRejected)]
    public void An_end_that_was_wanted_or_cannot_get_better_is_not_retried(CloseReason reason)
    {
        var schedule = new ManualSchedule();
        var session = Create(schedule);
        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, reason));

        Assert.Empty(schedule.Delays);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public void Retrying_stops_when_it_is_switched_off_cancelled_by_hand_or_exhausted()
    {
        var schedule = new ManualSchedule();
        var session = Create(schedule);
        session.Start();

        // Ein Server, der nie erreichbar war, wird nicht von selbst immer wieder versucht.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed));
        Assert.Empty(schedule.Delays);

        session.Reconnect();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.Equal(1, schedule.Waiting);

        // „Trennen“ ist gegenstandslos (sie ist schon zu) – aber von Hand neu verbinden sagt den Timer ab.
        var connections = _backend.Connections.Count;
        session.Reconnect();
        Assert.Equal(0, schedule.Waiting);
        Assert.Equal(connections + 1, _backend.Connections.Count);
        schedule.Elapse();
        Assert.Equal(connections + 1, _backend.Connections.Count);

        // Nach allen Versuchen ist Schluss – und es steht da.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        for (var attempt = 0; attempt < SessionRetry.Delays.Count; attempt++)
        {
            schedule.Elapse();
            _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectFailed));
        }

        Assert.Equal(0, schedule.Waiting);
        Assert.Contains("giving up", _terminal.ToString());

        // Ausgeschaltet: kein Versuch. Und eine geschlossene Sitzung versucht nichts mehr.
        schedule.Enabled = false;
        session.Reconnect();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.Equal(0, schedule.Waiting);

        schedule.Enabled = true;
        session.Reconnect();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.Equal(1, schedule.Waiting);
        connections = _backend.Connections.Count;
        session.Dispose();
        schedule.Elapse();
        Assert.Equal(connections, _backend.Connections.Count);
    }

    [Fact]
    public void Disconnecting_while_a_question_is_open_closes_the_dialog()
    {
        _prompts.NeverAnswer = true;
        var session = Create();
        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Authenticating));
        _backend.Last.Ask(new ConnectionPrompt.HostKey("10.0.1.10", 22, "ssh-ed25519", "SHA256:abc", false, null, null, _ => { }));
        Assert.Single(_prompts.Asked);

        session.Disconnect();

        Assert.Equal(1, _prompts.Cancelled);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public void An_account_without_a_shell_stays_connected_for_its_files()
    {
        var session = Create();
        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        var ended = 0;
        session.ConnectionEnded += (_, _) => ended++;

        // Der Server hat die Shell abgelehnt oder sofort beendet; die Verbindung steht weiter.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected, ExitStatus: 1, FilesOnly: true));

        Assert.True(session.IsFilesOnly);
        Assert.False(session.IsClosed);
        Assert.Equal(0, ended);
        Assert.Equal("Files only", session.StatusText);
        Assert.True(session.Inspector.IsOnline);
        Assert.Equal("Files only", session.Inspector.StatusText);
        Assert.Contains("does not allow a shell", _terminal.ToString());

        // Gesagt wird es einmal, und Getipptes geht nirgends hin.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected, ExitStatus: 1, FilesOnly: true));
        Assert.Equal(2, _terminal.ToString().Split("does not allow a shell").Length);
        session.Send("ls\r");
        Assert.Empty(_backend.Last.Input);

        // „Erneut verbinden“ beginnt von vorn – mit Shell, wenn der Server inzwischen eine erlaubt.
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        Assert.False(session.IsFilesOnly);
        session.Reconnect();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        Assert.False(session.IsFilesOnly);
        session.Send("ls\r");
        Assert.Equal(["ls\r"], _backend.Last.Input);
    }

    [Theory]
    [InlineData(CloseReason.AuthFailed, "no sign-in method was accepted", "Sign-in failed. (no sign-in method was accepted)")]
    [InlineData(CloseReason.ConnectFailed, "connection refused", "Server not reachable. (connection refused)")]
    [InlineData(CloseReason.HostKeyRejected, "the host key was not trusted", "The host key was not trusted.")]
    [InlineData(CloseReason.ConnectionLost, "", "Connection lost.")]
    [InlineData(CloseReason.User, "ignored", "Connection closed.")]
    public void A_closed_session_says_why(CloseReason reason, string? detail, string expected)
    {
        var session = Create();
        session.Start();

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, reason, detail));

        Assert.True(session.IsClosed);
        Assert.Equal(expected, session.StatusText);
        Assert.Contains(expected, _terminal.ToString());
        Assert.False(session.Inspector.IsOnline);
    }

    [Fact]
    public void A_shell_that_exits_with_an_error_reports_its_code()
    {
        var session = Create();
        session.Start();

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.Remote, ExitStatus: 0));
        Assert.Equal("Session ended.", session.StatusText);

        session.Reconnect();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.Remote, ExitStatus: 127));
        Assert.Equal("Session ended (code 127).", session.StatusText);
    }

    [Fact]
    public void Input_reaches_the_server_only_while_connected()
    {
        var session = Create();
        session.Start();
        session.Write(Encoding.UTF8.GetBytes("too early"));

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        session.Write(Encoding.UTF8.GetBytes("ls\r"));

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        session.Write(Encoding.UTF8.GetBytes("too late"));

        Assert.Equal(["ls\r"], _backend.Last.Input);
    }

    [Fact]
    public void Server_output_is_passed_on_unchanged()
    {
        var session = Create();
        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));
        _terminal.Clear();

        _backend.Last.Emit("ubuntu@web01:~$ ");

        Assert.Equal("ubuntu@web01:~$ ", _terminal.ToString());
    }

    [Fact]
    public void A_host_key_question_goes_to_the_user_and_the_answer_back()
    {
        var session = Create();
        session.Start();
        bool? answered = null;
        var prompt = new ConnectionPrompt.HostKey("10.0.1.10", 22, "ssh-ed25519", "SHA256:abc", false, null, null, accept => answered = accept);

        _prompts.TrustHostKey = true;
        _backend.Last.Ask(prompt);
        Assert.True(answered);

        _prompts.TrustHostKey = false;
        _backend.Last.Ask(prompt);
        Assert.False(answered);
        Assert.Equal([prompt, prompt], _prompts.Asked);
    }

    [Fact]
    public void A_secret_question_returns_the_entered_values()
    {
        var session = Create();
        session.Start();
        SecretAnswer? answered = null;
        var answers = 0;
        var prompt = new ConnectionPrompt.Secret(
            SecretKind.Password, "ubuntu", "10.0.1.10", null, null, null, [new SecretField("", false)], true, 1,
            answer =>
            {
                answered = answer;
                answers++;
            });

        _prompts.Secret = new SecretAnswer(["geheim"], Save: true);
        _backend.Last.Ask(prompt);
        Assert.Equal(new SecretAnswer(["geheim"], true).Values, answered!.Values);
        Assert.True(answered.Save);

        _prompts.Secret = null;
        _backend.Last.Ask(prompt);
        Assert.Null(answered);
        Assert.Equal(2, answers);
    }

    [Fact]
    public void A_question_still_open_when_the_connection_ends_is_withdrawn_unanswered()
    {
        var session = Create();
        session.Start();
        var answers = 0;
        _prompts.NeverAnswer = true;

        _backend.Last.Ask(new ConnectionPrompt.HostKey("h", 22, "a", "f", false, null, null, _ => answers++));
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));

        Assert.Equal(1, _prompts.Cancelled);
        Assert.Equal(0, answers);
    }

    [Fact]
    public void If_the_question_cannot_be_shown_it_counts_as_refused()
    {
        var failing = new ThrowingPrompts();
        var session = new SessionViewModel(Profile, _backend, new ImmediateDispatcher(), failing);
        session.Start();
        bool? trusted = null;
        var secretAnswered = false;
        SecretAnswer? secret = new([], false);

        _backend.Last.Ask(new ConnectionPrompt.HostKey("h", 22, "a", "f", false, null, null, accept => trusted = accept));
        _backend.Last.Ask(new ConnectionPrompt.Secret(
            SecretKind.Password, "u", "h", null, null, null, [], false, 1,
            answer =>
            {
                secret = answer;
                secretAnswered = true;
            }));

        Assert.False(trusted);
        Assert.True(secretAnswered);
        Assert.Null(secret);
    }

    [Fact]
    public void Reconnect_replaces_the_connection_and_keeps_the_terminal_size()
    {
        var session = Create();
        session.Resize(100, 30);
        session.Start();
        var first = _backend.Last;
        first.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));

        session.Reconnect();

        var second = _backend.Last;
        Assert.NotSame(first, second);
        Assert.True(first.Disposed);
        Assert.True(second.Started);
        Assert.Equal((100, 30), second.Size);

        // Nachzügler der alten Verbindung ändern nichts mehr.
        second.Set(new ConnectionStatus(ConnectionState.Connected));
        first.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.AuthFailed));
        first.Emit("stale");
        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.DoesNotContain("stale", _terminal.ToString());
    }

    [Fact]
    public void Reconnect_before_the_first_start_does_nothing()
    {
        var session = Create();

        session.Reconnect();

        Assert.Single(_backend.Connections);
        Assert.False(_backend.Last.Started);
    }

    [Fact]
    public void Disposing_the_session_ends_its_connection()
    {
        var session = Create();
        session.Start();

        session.Dispose();
        session.Dispose();

        Assert.True(_backend.Last.Disposed);
    }

    [Fact]
    public void With_a_file_system_the_browser_loads_on_connect_and_empties_on_close()
    {
        using var mock = new MockBackend();
        var session = new SessionViewModel(mock.GetProfiles()[0], mock, new ImmediateDispatcher(), _prompts);
        Assert.False(session.Files.IsAvailable);

        session.Start();

        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.True(session.Files.IsAvailable);
        Assert.Equal("/var/www", session.Files.CurrentPath);
        Assert.NotEmpty(session.Files.Entries);
        Assert.True(session.Inspector.HasMetrics);
        Assert.Equal("Ubuntu 24.04.1 LTS", session.Inspector.OsName);
    }

    [Fact]
    public void Without_a_file_system_the_browser_stays_unavailable()
    {
        var session = Create();
        session.Start();

        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        Assert.True(session.Files.IsUnavailable);
        Assert.Empty(session.Files.Entries);
        Assert.False(session.Inspector.HasMetrics);
    }

    [Fact]
    public void The_banner_of_the_server_is_printed_as_plain_text()
    {
        var session = Create();
        session.Start();

        // Wer den Banner schickt, ist noch nicht angemeldet: Escape-Folgen darin dürfen das Terminal nicht steuern.
        _backend.Last.ShowBanner("Authorized use only.\n\u001b[2J\u001b]0;evil\u0007Second line\r\n\n");

        Assert.EndsWith("Authorized use only.\r\n[2J]0;evilSecond line\r\n", _terminal.ToString());
        Assert.DoesNotContain('\u001b', _terminal.ToString()[_terminal.ToString().IndexOf("Authorized", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void A_banner_of_a_replaced_connection_is_ignored()
    {
        var session = Create();
        session.Start();
        var old = _backend.Last;
        old.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        session.Reconnect();
        var before = _terminal.Length;

        old.ShowBanner("stale");

        Assert.Equal(before, _terminal.Length);
    }

    [Fact]
    public void Pausing_reaches_the_current_connection()
    {
        var session = Create();
        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        session.SetPaused(true);
        session.SetPaused(false);

        Assert.Equal([true, false], _backend.Last.Paused);
    }

    [Fact]
    public void Text_is_sent_as_if_it_had_been_typed()
    {
        var session = Create();
        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Connected));

        session.Send("echo grüße\r");

        Assert.Equal(["echo grüße\r"], _backend.Last.Input);
    }

    [Fact]
    public void Every_connect_asks_anew_which_connection_to_share()
    {
        var standing = new ScriptedConnection(Profile, null);
        ScriptedConnection? offer = standing;
        var asked = 0;
        var session = new SessionViewModel(Profile, _backend, new ImmediateDispatcher(), _prompts, asking =>
        {
            asked++;
            return offer;
        });
        Assert.Same(standing, _backend.Last.Shared);

        session.Start();
        _backend.Last.Set(new ConnectionStatus(ConnectionState.Closed, CloseReason.ConnectionLost));
        offer = null;
        session.Reconnect();

        Assert.Null(_backend.Last.Shared);
        Assert.Equal(2, asked);
    }

    private sealed class ThrowingPrompts : IUserPrompts
    {
        public Task<bool> ConfirmHostKeyAsync(ConnectionPrompt.HostKey prompt, CancellationToken cancellation)
            => throw new InvalidOperationException("no window");

        public Task<SecretAnswer?> AskSecretAsync(ConnectionPrompt.Secret prompt, CancellationToken cancellation)
            => throw new InvalidOperationException("no window");
    }
}
